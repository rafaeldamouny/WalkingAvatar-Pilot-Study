using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;

namespace WalkingEyeExperiment.Editor
{
    /// <summary>
    /// Adds the downloaded CGTrader corridor to the experiment scene, aligns its
    /// hallway with the participant view, places the existing realistic avatar in
    /// the middle, and disables passthrough so the scene is fully virtual.
    /// </summary>
    public static class CorridorEnvironmentSetup
    {
        const string ScenePath = "Assets/Scenes/NeonWalkingExperiment.unity";
        const string CorridorPath = "Assets/Resources/ExperimentEnvironment/office.fbx";
        const string TextureRoot = "Assets/Resources/ExperimentEnvironment/Textures/";
        const string MaterialRoot = "Assets/Resources/ExperimentEnvironment/Materials/";

        [MenuItem("Experiment Integration/Add Corridor and Center Avatar")]
        public static void Apply()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            FixCorridorMaterials();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var platform = UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
            if (!platform) throw new InvalidOperationException("Seated Walking Experiment was not found in the target scene.");
            if (!platform.avatarPrefab) throw new InvalidOperationException("The existing realistic avatar prefab is not assigned.");

            var corridorAsset = AssetDatabase.LoadAssetAtPath<GameObject>(CorridorPath);
            if (!corridorAsset) throw new InvalidOperationException("Unity could not import office.fbx from " + CorridorPath);

            RemoveRoot(scene, "CGTrader Corridor Environment");
            RemoveRoot(scene, "Corridor Center Avatar Preview");

            var environment = new GameObject("CGTrader Corridor Environment");
            SceneManager.MoveGameObjectToScene(environment, scene);
            var model = PrefabUtility.InstantiatePrefab(corridorAsset, scene) as GameObject;
            if (!model) throw new InvalidOperationException("Could not instantiate the imported corridor FBX.");
            model.name = "Corridor FBX";
            model.transform.SetParent(environment.transform, false);

            Bounds bounds = CalculateBounds(environment);
            // The source corridor is authored along X. Rotate it so it faces the
            // participant's forward direction in Unity (+Z).
            if (bounds.size.x > bounds.size.z * 1.2f)
                environment.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            bounds = CalculateBounds(environment);
            // Center the corridor on the participant, put its floor on Y=0, and
            // leave a short approach before its entrance.
            environment.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, 1.0f - bounds.min.z);
            bounds = CalculateBounds(environment);

            foreach (var renderer in environment.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            foreach (var filter in environment.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.sharedMesh || filter.GetComponent<Collider>()) continue;
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;
            }
            GameObjectUtility.SetStaticEditorFlags(environment, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);

            var avatar = PrefabUtility.InstantiatePrefab(platform.avatarPrefab, scene) as GameObject;
            if (!avatar) throw new InvalidOperationException("Could not instantiate the existing realistic avatar prefab.");
            avatar.name = "Corridor Center Avatar Preview";
            avatar.transform.position = new Vector3(0f, 0f, (bounds.min.z + bounds.max.z) * 0.5f);
            avatar.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            var animator = avatar.GetComponentInChildren<Animator>(true);
            if (!animator) animator = avatar.AddComponent<Animator>();
            if (platform.avatarRigAsset) animator.avatar = platform.avatarRigAsset;
            if (platform.animationController) animator.runtimeAnimatorController = platform.animationController;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var avatarBounds = CalculateBounds(avatar);
            avatar.transform.position += Vector3.up * -avatarBounds.min.y;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            var previewToggle = environment.AddComponent<CorridorAvatarPreviewToggle>();
            previewToggle.platform = platform;
            previewToggle.previewAvatar = avatar;

            // This experiment scene is virtual-only. Do not expose the room camera
            // or enable passthrough overlays behind the imported environment.
            platform.passthroughRoom = false;
            var rig = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig)
            {
                var manager = rig.GetComponent<OVRManager>();
                if (manager) manager.isInsightPassthroughEnabled = false;
                foreach (var layer in rig.GetComponentsInChildren<OVRPassthroughLayer>(true))
                    layer.enabled = false;
            }
            if (platform.participantCamera)
            {
                platform.participantCamera.clearFlags = CameraClearFlags.SolidColor;
                platform.participantCamera.backgroundColor = new Color(0.025f, 0.025f, 0.035f, 1f);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = environment;
            if (SceneView.lastActiveSceneView)
            {
                SceneVisibilityManager.instance.Show(environment, true);
                var sceneView = SceneView.lastActiveSceneView;
                sceneView.LookAt(bounds.center, Quaternion.Euler(12f, 180f, 0f), Mathf.Max(bounds.size.magnitude * 0.9f, 4f));
                sceneView.Repaint();
            }
            Debug.Log($"[CorridorSetup] Added corridor {bounds.size.x:F2} x {bounds.size.y:F2} x {bounds.size.z:F2} m; centered preview avatar at z={(bounds.min.z + bounds.max.z) * 0.5f:F2} m; passthrough disabled.");
        }

        [MenuItem("Experiment Integration/Fix Corridor Textures")]
        public static void FixCorridorMaterials()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (!shader) throw new InvalidOperationException("Could not find a supported lit shader for the corridor.");

            var floor = LoadTexture("Floor Texture/WoodFlooring061_COL_3K.jpg");
            var wall = LoadTexture("Wall Texture/WoodFineDark004_COL_3K.jpg");
            var plant = LoadTexture("Plant Texture/bpng.jpg");
            var pot = LoadTexture("Plant Texture/Pot textures_col.jpg");
            var stool = LoadTexture("Chair Texture/wooden stool texture(F).png");
            var pictureFiles = new[]
            {
                "Picture photos/attack_on_titan__7_minutes_in_heaven___levi_by_vanillametal-d88zah7.jpg",
                "Picture photos/images.jpg", "Picture photos/index.jpg", "Picture photos/madara.jpg",
                "Picture photos/main-qimg-482b9dab177350d9093eedea611a9372.jpg"
            };
            var pictures = new Texture2D[pictureFiles.Length];
            for (int i = 0; i < pictureFiles.Length; i++) pictures[i] = LoadTexture(pictureFiles[i]);

            var guids = AssetDatabase.FindAssets("t:Material", new[] { MaterialRoot.TrimEnd('/') });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!material) continue;
                var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                material.shader = shader;
                Texture2D albedo = null;
                if (name.Contains("floor")) albedo = floor;
                else if (name.Contains("blatt") || name.Contains("plant")) albedo = plant;
                else if (name.Contains("pot")) albedo = pot;
                else if (name.Contains("stool") || name.Contains("chair")) albedo = stool;
                else if (name.Contains("picture"))
                {
                    int index = PictureIndex(name, pictures.Length);
                    albedo = pictures[index];
                }
                else if (name.Contains("frame") || name.Contains("wood") || name.Contains("material")) albedo = wall;

                if (albedo) SetBaseTexture(material, albedo);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.22f);
                EditorUtility.SetDirty(material);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[CorridorSetup] Applied downloaded corridor color textures to {guids.Length} materials.");
        }

        static Texture2D LoadTexture(string relativePath)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TextureRoot + relativePath);
            if (!texture) Debug.LogWarning("[CorridorSetup] Missing corridor texture: " + TextureRoot + relativePath);
            return texture;
        }

        static int PictureIndex(string name, int count)
        {
            if (count <= 1) return 0;
            int index = 0;
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (char.IsDigit(name[i])) { index = name[i] - '0'; break; }
            }
            return Mathf.Clamp(index, 0, count - 1);
        }

        static void SetBaseTexture(Material material, Texture texture)
        {
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        }

        static void RemoveRoot(Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name) UnityEngine.Object.DestroyImmediate(root);
        }

        static Bounds CalculateBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("No renderers found under " + root.name);
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }
    }
}
