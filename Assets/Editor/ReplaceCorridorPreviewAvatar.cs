using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
static class ReplaceCorridorPreviewAvatar
{
    const string PreviewName = "Corridor Center Avatar Preview";
    const string AvatarPath = "Assets/RP_Character/rp_eric_rigged_001/rp_eric_rigged_001_u3d.fbx";
    const string ControllerPath = "Assets/Walks_01/MotusMan Animator Controller.controller";
    const string RunOnceMarker = "Assets/Editor/REPLACE_CORRIDOR_PREVIEW.once";

    static ReplaceCorridorPreviewAvatar()
    {
        EditorApplication.delayCall += RunIfRequested;
    }

    [MenuItem("Experiment Integration/Replace Corridor Preview with RP Avatar")]
    static void ReplaceFromMenu() => Replace();

    static void RunIfRequested()
    {
        if (!System.IO.File.Exists(RunOnceMarker)) return;
        System.IO.File.Delete(RunOnceMarker);
        AssetDatabase.Refresh();
        Replace();
    }

    static void Replace()
    {
        var scene = SceneManager.GetActiveScene();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
        if (!scene.IsValid() || !scene.isLoaded || !model || !controller)
        {
            Debug.LogError("Could not replace the corridor preview: scene, RP avatar, or saved walk controller is missing.");
            return;
        }

        var old = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .FirstOrDefault(go => go.name == PreviewName);
        if (!old)
        {
            Debug.LogError("Could not replace the corridor preview because 'Corridor Center Avatar Preview' was not found in the open scene.");
            return;
        }

        var oldAnimator = old.GetComponentInChildren<Animator>(true);
        var existingAvatarName = oldAnimator && oldAnimator.avatar ? oldAnimator.avatar.name : string.Empty;
        if (existingAvatarName.IndexOf("rp_eric_rigged_001", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Debug.Log("The corridor preview already uses the RP Eric avatar.");
            return;
        }

        var parent = old.transform.parent;
        var siblingIndex = old.transform.GetSiblingIndex();
        var position = old.transform.position;
        var rotation = old.transform.rotation;
        var scale = old.transform.localScale;
        var active = old.activeSelf;
        var replacement = PrefabUtility.InstantiatePrefab(model, scene) as GameObject;
        if (!replacement)
        {
            Debug.LogError("Unity could not instantiate the RP Eric FBX prefab.");
            return;
        }

        if (parent) replacement.transform.SetParent(parent, false);
        replacement.name = PreviewName;
        replacement.transform.position = position;
        replacement.transform.rotation = rotation;
        replacement.transform.localScale = scale;
        replacement.transform.SetSiblingIndex(siblingIndex);
        replacement.SetActive(active);

        var avatar = AssetDatabase.LoadAllAssetsAtPath(AvatarPath)
            .OfType<Avatar>().FirstOrDefault(a => a && a.isHuman);
        var animator = replacement.GetComponentInChildren<Animator>(true);
        if (!animator) animator = replacement.AddComponent<Animator>();
        if (avatar) animator.avatar = avatar;
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var renderers = replacement.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            var targetFloorY = position.y;
            replacement.transform.position += Vector3.up * (targetFloorY - bounds.min.y);
        }
        foreach (var renderer in renderers)
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }

        Undo.DestroyObjectImmediate(old);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("Replaced the old corridor preview with RP Eric, kept its corridor placement and Reallusion walk controller, and aligned the feet to the old floor height.");
    }
}
