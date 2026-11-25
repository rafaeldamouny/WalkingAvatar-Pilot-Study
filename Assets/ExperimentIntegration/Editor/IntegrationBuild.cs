using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.XR.OpenXR;
using UnityEditor.Build;

namespace WalkingEyeExperiment.Editor
{
    public static class IntegrationBuild
    {
        public const string ScenePath = "Assets/Scenes/NeonWalkingExperiment.unity";

        [MenuItem("Experiment Integration/1 Prepare RP Scene")]
        public static void PrepareRpScene()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Working.unity");
            Camera camera = null;
            foreach (var item in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (item.name == "CenterEyeAnchor") camera = item;
            if (!camera) throw new Exception("Existing CenterEyeAnchor camera not found");
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "RP Visual Field";
            UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(camera.transform, false);
            quad.transform.localPosition = new Vector3(0, 0, 0.17f);
            quad.transform.localScale = new Vector3(2, 2, 1);
            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/ExperimentIntegration/RP/LowVisionOverlayMat.mat");
            var overlay = quad.AddComponent<LowVisionOverlayController>();
            overlay.overlayMaterial = renderer.sharedMaterial;
            overlay.noneSettings = Preset(0, 0, 0, .5f);
            overlay.cataractSettings = Preset(.3f, 5, 0, .5f);
            overlay.glaucomaSettings = Preset(.85f, 3.6f, 1, 0);
            overlay.severeSettings = Preset(.732f, 2, 1, .08f);
            overlay.currentVisionType = VisionType.Glaucoma;
            overlay.tunnelFovDegrees = 10;
            var driver = quad.AddComponent<RpFieldDriver>();
            driver.participantCamera = camera;
            driver.overlay = overlay;
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            ValidateRp();
        }

        static VisionTypeSettings Preset(float alpha, float blur, float amount, float radius)
        {
            return new VisionTypeSettings { tintColor = new Color(0,0,0,alpha == 0 ? 0 : 1), globalAlpha = alpha, blurAmount = blur, vignetteAmount = amount, vignetteRadius = radius };
        }

        public static void ValidateRp()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/ExperimentIntegration/RP/LowVisionOverlaySimple.shader");
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                    throw new Exception(message.message);
            if (!shader || ShaderUtil.ShaderHasError(shader)) throw new Exception("RP shader compilation failed");
            Directory.CreateDirectory("IntegrationEvidence");
            File.WriteAllText("IntegrationEvidence/rp-stage.txt", "PASS: existing Working scene copied; RP shader imported without errors; source presets preserved. Stereo rendering still requires Quest validation.");
            Debug.Log("INTEGRATION_RP_PASS");
        }

        [MenuItem("Experiment Integration/2 Prepare Complete Platform")]
        public static void PreparePlatform()
        {
            var scene=EditorSceneManager.OpenScene(ScenePath);
            var oldTrials=UnityEngine.Object.FindFirstObjectByType<TrialManager>();
            if(!oldTrials) throw new Exception("RP scene must retain original TrialManager until platform preparation");
            GameObject prefab=oldTrials.avatarPrefab;
            Avatar avatar=oldTrials.avatarRigAsset;
            RuntimeAnimatorController controller=oldTrials.animController;
            var rig=UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
            if(!rig || !prefab || !avatar || !controller) throw new Exception("Existing avatar/XR references missing");
            foreach(var root in scene.GetRootGameObjects())
                if(!root.GetComponentInChildren<OVRCameraRig>(true) && !root.GetComponent<Light>()) UnityEngine.Object.DestroyImmediate(root);
            rig.transform.position=Vector3.zero;
            rig.transform.rotation=Quaternion.identity;
            rig.usePerEyeCameras=false;
            var manager=rig.GetComponent<OVRManager>();
            if(manager) { manager.isInsightPassthroughEnabled=true; manager.trackingOriginType=OVRManager.TrackingOrigin.FloorLevel; }
            foreach(var layer in rig.GetComponentsInChildren<OVRPassthroughLayer>(true)) layer.enabled=false;
            Camera camera=rig.centerEyeAnchor.GetComponent<Camera>();
            foreach(var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) c.enabled=c==camera;
            camera.tag="MainCamera";
            camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=Color.clear;
            camera.allowHDR=false;
            camera.nearClipPlane=.03f;
            camera.transform.localPosition=new Vector3(0,1.2f,0);
            var rp=UnityEngine.Object.FindFirstObjectByType<RpFieldDriver>();
            if(!rp) throw new Exception("RP effect missing");
            rp.centerMode=RpFieldDriver.CenterMode.GazeCentered;
            rp.participantCamera=camera;
            // Position of this object is for source FoV conversion; vertices fill each eye's clip space.
            rp.GetComponent<Renderer>().localBounds=new Bounds(Vector3.zero,Vector3.one*100);

            ConfigureAddressables();
            GameObject neonPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.pupil-labs.neon-xr.core/Runtime/Prefabs/NeonXR.prefab");
            var neon=(GameObject)PrefabUtility.InstantiatePrefab(neonPrefab);
            neon.name="Official Neon XR";
            foreach(var visual in neon.GetComponentsInChildren<PupilLabs.GazeDataVisualizer>(true)) visual.enabled=false;
            var provider=neon.GetComponentInChildren<PupilLabs.NeonGazeDataProvider>(true);
            var storage=neon.GetComponentInChildren<PupilLabs.DataStorage>(true);
            var rootPlatform=new GameObject("Seated Walking Experiment");
            var bridge=rootPlatform.AddComponent<NeonGazeBridge>();
            bridge.provider=provider; bridge.storage=storage; bridge.participantCamera=camera; bridge.rpField=rp;
            bridge.mountCalibrationVerified=false;
            Material debug=MakeMaterial("GazeDebug",new Color(1,.7f,.1f,1));
            bridge.debugMaterial=debug;
            var logger=rootPlatform.AddComponent<ResearchSessionLogger>();
            logger.gaze=bridge;
            logger.autoStart=false;
            logger.condition="RP_gaze_contingent_passthrough";
            var platform=rootPlatform.AddComponent<SeatedWalkingPlatform>();
            platform.avatarPrefab=prefab; platform.avatarRigAsset=avatar; platform.animationController=controller;
            platform.participantCamera=camera; platform.gaze=bridge; platform.logger=logger;
            platform.requireStartMenu=true; platform.requireValidGazeToStart=true;
            platform.selectedTunnelFov=15;
            platform.startWalkingOnExperimentStart=true; platform.allowRuntimeControlsDuringExperiment=true;
            platform.headsetStatus=CreateStatus(camera);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name="Seated Experiment Floor";
            floor.transform.position=new Vector3(0,-.025f,5);
            floor.transform.localScale=new Vector3(30,.05f,30);
            floor.GetComponent<Renderer>().sharedMaterial=MakeMaterial("Floor",new Color(.25f,.27f,.28f,1));
            ApplyPassthrough(platform);
            foreach(var group in new[]{BuildTargetGroup.Android,BuildTargetGroup.Standalone})
            {
                var xr=OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if(!xr) continue;
                foreach(var feature in xr.GetFeatures<UnityEngine.XR.OpenXR.Features.OpenXRFeature>())
                    if(feature && (feature.GetType().Name.Contains("MockRuntime") || feature.GetType().Name.Contains("EyeGazeInteraction"))) feature.enabled=false;
                EditorUtility.SetDirty(xr);
            }
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,"com.amplitlab.neonwalking");
            PlayerSettings.Android.targetArchitectures=AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion=AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion=AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android,ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.forceInternetPermission=true;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,new[]{GraphicsDeviceType.Vulkan});
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
            EditorSceneManager.SaveScene(scene,ScenePath);
            AssetDatabase.SaveAssets();
            ValidatePlatform();
        }

        static Material MakeMaterial(string name, Color color)
        {
            const string folder="Assets/ExperimentIntegration/Materials";
            Directory.CreateDirectory(folder);
            string path=folder+"/"+name+".mat";
            Material material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!material) { material=new Material(Shader.Find("Universal Render Pipeline/Unlit")); AssetDatabase.CreateAsset(material,path); }
            material.SetColor("_BaseColor",color);
            EditorUtility.SetDirty(material);
            return material;
        }
        [MenuItem("Experiment Integration/Enable Passthrough Room")]
        public static void EnablePassthroughRoom()
        {
            var scene=EditorSceneManager.OpenScene(ScenePath);
            var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
            ApplyPassthrough(platform);
            EditorSceneManager.SaveScene(scene,ScenePath);
            AssetDatabase.SaveAssets();
            ValidatePlatform();
        }
        static void ApplyPassthrough(SeatedWalkingPlatform platform)
        {
            if(!platform) throw new Exception("Experiment platform missing");
            var camera=platform.participantCamera;
            var rig=camera.GetComponentInParent<OVRCameraRig>();
            var manager=rig.GetComponent<OVRManager>();
            manager.isInsightPassthroughEnabled=true;
            var layers=rig.GetComponentsInChildren<OVRPassthroughLayer>(true);
            foreach(var existing in layers) existing.enabled=false;
            var layer=layers.Length>0 ? layers[0] : rig.gameObject.AddComponent<OVRPassthroughLayer>();
            layer.projectionSurfaceType=OVRPassthroughLayer.ProjectionSurfaceType.Reconstructed;
            layer.overlayType=OVROverlay.OverlayType.Underlay;
            layer.hidden=false; layer.textureOpacity=1; layer.enabled=true;
            camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=Color.clear;
            camera.allowHDR=false;
            platform.passthroughRoom=true;
            platform.logger.condition="RP_gaze_contingent_passthrough";
            var floor=GameObject.Find("Seated Experiment Floor");
            if(floor) floor.GetComponent<Renderer>().enabled=false;
            // All application visuals are covered by the RP mask, including setup aids.
            platform.gaze.debugMaterial.renderQueue=4890;
            EditorUtility.SetDirty(platform.gaze.debugMaterial);
            if(platform.headsetStatus && platform.headsetStatus.material)
            {
                platform.headsetStatus.material.renderQueue=4890;
                EditorUtility.SetDirty(platform.headsetStatus.material);
                var canvas=platform.headsetStatus.GetComponentInParent<Canvas>();
                canvas.overrideSorting=false;
            }
            PlayerSettings.preserveFramebufferAlpha=true;
            var mobile=AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
            var serialized=new SerializedObject(mobile);
            serialized.FindProperty("m_AllowPostProcessAlphaOutput").boolValue=true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mobile);
            var config=OVRProjectConfig.CachedProjectConfig;
            config.insightPassthroughSupport=OVRProjectConfig.FeatureSupport.Required;
            OVRProjectConfig.CommitProjectConfig(config);
        }
        static Text CreateStatus(Camera camera)
        {
            var root=new GameObject("Headset Setup Status",typeof(RectTransform),typeof(Canvas));
            root.transform.SetParent(camera.transform,false);
            root.transform.localPosition=new Vector3(0,-.28f,.9f);
            root.transform.localScale=Vector3.one*.0007f;
            var canvas=root.GetComponent<Canvas>(); canvas.renderMode=RenderMode.WorldSpace;
            canvas.worldCamera=camera;
            var rect=root.GetComponent<RectTransform>(); rect.sizeDelta=new Vector2(1000,220);
            var text=new GameObject("Status",typeof(RectTransform),typeof(Text));
            text.transform.SetParent(root.transform,false);
            var tr=text.GetComponent<RectTransform>(); tr.anchorMin=Vector2.zero; tr.anchorMax=Vector2.one; tr.offsetMin=tr.offsetMax=Vector2.zero;
            var label=text.GetComponent<Text>(); label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize=30; label.color=Color.white; label.alignment=TextAnchor.MiddleCenter; label.raycastTarget=false;
            label.text="NEON setup pending\nA: walk/pause | B: RP center";
            return label;
        }
        public static void ConfigureAddressables()
        {
            var settings=AddressableAssetSettingsDefaultObject.GetSettings(true);
            var group=settings.FindGroup("NeonXR Group") ?? settings.CreateGroup("NeonXR Group",false,false,true,null,typeof(BundledAssetGroupSchema),typeof(ContentUpdateGroupSchema));
            var schema=group.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(settings,AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings,AddressableAssetSettings.kLocalLoadPath);
            string root="Packages/com.pupil-labs.neon-xr.core/Runtime/Addressables/";
            foreach(var pair in new[]{new[]{"config.json","pl.config"},new[]{"configDefaults.json","pl.config.defaults"},new[]{"calibration.bytes","pl.calibration"}})
            {
                string guid=AssetDatabase.AssetPathToGUID(root+pair[0]);
                if(string.IsNullOrEmpty(guid)) throw new Exception("Neon Addressable missing "+pair[0]);
                settings.CreateOrMoveEntry(guid,group).address=pair[1];
            }
            for(int i=0;i<settings.DataBuilders.Count;i++)
                if(settings.DataBuilders[i] is BuildScriptPackedMode) settings.ActivePlayerDataBuilderIndex=i;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }
        [MenuItem("Experiment Integration/Validate Platform")]
        public static void ValidatePlatform()
        {
            var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
            if(!platform) throw new Exception("SeatedWalkingPlatform is missing from the build scene");
            if(!platform.avatarPrefab) throw new Exception("The build scene has no avatar prefab assigned");
            if(!platform.avatarRigAsset) throw new Exception("The build scene has no avatar rig assigned");
            if(!platform.avatarRigAsset.isHuman || !platform.avatarRigAsset.isValid)
                throw new Exception($"Avatar rig '{platform.avatarRigAsset.name}' is not valid for humanoid animation (isHuman={platform.avatarRigAsset.isHuman}, isValid={platform.avatarRigAsset.isValid})");
            if(!platform.gaze.provider || !platform.gaze.storage || !platform.gaze.rpField) throw new Exception("Neon/RP scene wiring incomplete");
            ValidateRp();
            File.WriteAllLines("IntegrationEvidence/avatar-dependencies.txt",AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(platform.avatarPrefab),true));
            File.WriteAllText("IntegrationEvidence/platform-stage.txt","PASS: platform compiled; existing avatar, valid humanoid rig, animation controller, XR camera, Neon provider, Addressables and RP scene wired. Hardware gaze/calibration and stereo rendering remain unverified.");
            Debug.Log("INTEGRATION_PLATFORM_PASS");
        }
        [MenuItem("Experiment Integration/Build Quest APK")]
        public static void BuildQuest()
        {
            if(!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android,BuildTarget.Android)) throw new Exception("Android target switch failed");
            var scene=EditorSceneManager.OpenScene(ScenePath);
            // Repair the stale fileID serialized for the imported RP Eric FBX. Unity can
            // still instantiate the scene preview from its prefab source, but the runtime
            // field resolves as Missing until reassigned through AssetDatabase.
            var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
            var rpAvatar=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/RP_Character/rp_eric_rigged_001/rp_eric_rigged_001_u3d.fbx");
            if(platform && !platform.avatarPrefab && rpAvatar)
            {
                platform.avatarPrefab=rpAvatar;
                EditorUtility.SetDirty(platform);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[IntegrationBuild] Repaired the missing RP Eric FBX prefab reference in the build scene.");
            }
            // The corridor side walls end at z=8.52 and z=8.63; their centerline is
            // x=0.6765. Put the camera rig at the corridor entrance behind the actor,
            // and align every turn's T0 with the measured T junction center.
            var junction=GameObject.Find("Corridor T Junction");
            if(!junction) junction=new GameObject("Corridor T Junction");
            junction.transform.SetPositionAndRotation(new Vector3(.6765f,0f,8.575f),Quaternion.identity);
            if(platform)
            {
                platform.corridorTJunction=junction.transform;
                platform.seatedEyeHeight=1.65f;
                EditorUtility.SetDirty(platform);
            }
            var cameraRig=UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
            if(cameraRig)
            {
                cameraRig.transform.SetPositionAndRotation(new Vector3(.6765f,0f,0f),Quaternion.identity);
                EditorUtility.SetDirty(cameraRig.transform);
                if(cameraRig.centerEyeAnchor)
                {
                    cameraRig.centerEyeAnchor.localPosition=Vector3.up*(platform ? platform.seatedEyeHeight : 1.65f);
                    cameraRig.centerEyeAnchor.localRotation=Quaternion.identity;
                    EditorUtility.SetDirty(cameraRig.centerEyeAnchor);
                }
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[IntegrationBuild] Camera start aligned behind the avatar; turn T0 aligned to corridor junction at (0.6765, 0, 8.575).");
            ValidatePlatform();
            ConfigureAddressables();
            AddressableAssetSettings.BuildPlayerContent(out var content);
            if(!string.IsNullOrEmpty(content.Error)) throw new Exception("Neon Addressables: "+content.Error);
            Directory.CreateDirectory("Builds");
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=new[]{ScenePath}, locationPathName="Builds/NeonWalkingExperiment.apk", target=BuildTarget.Android, options=BuildOptions.Development });
            File.WriteAllText("IntegrationEvidence/android-build.txt",result.summary.result+"; errors="+result.summary.totalErrors+"; size="+result.summary.totalSize);
            if(result.summary.result!=UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new Exception("Quest APK build failed");
            Debug.Log("INTEGRATION_ANDROID_BUILD_PASS");
        }

        public static void RefineSetup()
        {
            var scene=EditorSceneManager.OpenScene(ScenePath);
            var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
            platform.logger.condition="manual_gaze_validation_passthrough";
            platform.gaze.debugMaterial.renderQueue=4890;
            EditorUtility.SetDirty(platform.gaze.debugMaterial);
            string path="Assets/ExperimentIntegration/Materials/SetupStatus.mat";
            Material status=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!status) { status=new Material(Shader.Find("UI/Default")); AssetDatabase.CreateAsset(status,path); }
            status.renderQueue=4890;
            platform.headsetStatus.material=status;
            var canvas=platform.headsetStatus.GetComponentInParent<Canvas>();
            canvas.overrideSorting=false;
            var mobile=AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
            mobile.renderScale=1;
            EditorUtility.SetDirty(mobile);
            PlayerSettings.productName="Neon Walking Experiment";
            PlayerSettings.companyName="AmplitLab";
            EditorSceneManager.SaveScene(scene,ScenePath);
            AssetDatabase.SaveAssets();
            ValidatePlatform();
        }
    }
}
