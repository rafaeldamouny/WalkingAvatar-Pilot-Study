using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PupilLabs;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Globalization;

namespace WalkingEyeExperiment.Editor
{
    [InitializeOnLoad]
    public static class IntegrationChecks
    {
        const string StateKey="WalkingEyeExperiment.ValidationState";
        static double phaseAt;
        static Vector3 initialPosition;
        static readonly List<string> findings=new List<string>();
        static IntegrationChecks()
        {
            if(SessionState.GetInt(StateKey,0)>0) EditorApplication.update+=Tick;
        }
        [MenuItem("Experiment Integration/Run Platform Checks")]
        public static void Run()
        {
            EditorSceneManager.OpenScene(IntegrationBuild.ScenePath);
            var neon=GameObject.Find("Official Neon XR");
            neon.SetActive(false); // Hardware-independent checks never start the network receiver.
            var gaze=UnityEngine.Object.FindFirstObjectByType<NeonGazeBridge>();
            gaze.useSyntheticGaze=true;
            gaze.syntheticAnglesDegrees=new Vector2(15,10);
            var logger=UnityEngine.Object.FindFirstObjectByType<ResearchSessionLogger>();
            logger.allowSyntheticRecording=true;
            logger.autoStart=true;
            var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
            platform.requireStartMenu=false;
            platform.showStatus=false;
            SessionState.SetInt(StateKey,1);
            SessionState.SetString(StateKey+"Started",DateTime.UtcNow.ToString("O"));
            EditorApplication.update-=Tick; EditorApplication.update+=Tick;
            EditorApplication.isPlaying=true;
        }
        static void Require(bool value,string message)
        { if(!value) throw new Exception(message); findings.Add("PASS: "+message); }

        public static void ValidateWindowSmoothing()
        {
            EditorSceneManager.OpenScene(IntegrationBuild.ScenePath);
            findings.Clear();
            CheckSmoothing();
            CheckTrackingInterruptions(UnityEngine.Object.FindFirstObjectByType<NeonGazeBridge>());
            File.WriteAllLines("IntegrationEvidence/window-smoothing-checks.txt",findings.Concat(new[]{"Focused editor checks; on-device smoothness requires participant confirmation."}));
            Debug.Log("INTEGRATION_WINDOW_SMOOTHING_PASS");
        }

        static void Tick()
        {
            int state=SessionState.GetInt(StateKey,0);
            if(state==0) { EditorApplication.update-=Tick; return; }
            EditorApplication.QueuePlayerLoopUpdate();
            if(!EditorApplication.isPlaying)
            {
                if(state==5) { SessionState.SetInt(StateKey,0); EditorApplication.Exit(0); }
                return;
            }
            try
            {
                if((DateTime.UtcNow-DateTime.Parse(SessionState.GetString(StateKey+"Started",DateTime.UtcNow.ToString("O")))).TotalSeconds>120)
                    throw new Exception("Play-mode checks timed out at state "+state+", time="+Time.time+", paused="+EditorApplication.isPaused);
                var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
                var gaze=platform.gaze;
                if(state==3 && gaze.avatarRegions)
                {
                    var headProxy=gaze.avatarRegions.Colliders.First(c=>c.GetComponent<AvatarBodyRegion>().region==BodyRegion.Head);
                    Vector3 local=gaze.participantCamera.transform.InverseTransformDirection(headProxy.bounds.center-gaze.participantCamera.transform.position);
                    gaze.syntheticAnglesDegrees=new Vector2(Mathf.Atan2(local.x,local.z)*Mathf.Rad2Deg,Mathf.Atan2(local.y,new Vector2(local.x,local.z).magnitude)*Mathf.Rad2Deg);
                }
                if(state==1 && Time.time>=2)
                {
                    CheckSmoothing();
                    CheckTrackingInterruptions(gaze);
                    CheckPassthrough(gaze);
                    Require(platform.AvatarInstance!=null,"Existing avatar spawned");
                    var horizontal=platform.AvatarInstance.position-platform.participantCamera.transform.position;
                    horizontal.y=0;
                    Require(Mathf.Abs(horizontal.magnitude-2)<.05f,"Avatar starts at two meters and remains paused");
                    Require(platform.AvatarInstance.GetComponentInChildren<Animator>().speed==0,"Paused avatar animation is frozen");
                    platform.ToggleWalk();
                    Require(platform.logger.trialId=="platform_1","First A press begins a logged walking trial");
                    SessionState.SetInt(StateKey,6);
                }
                else if(state==6 && Time.time>=2.5f)
                {
                    initialPosition=platform.AvatarInstance.position;
                    var animator=platform.AvatarInstance.GetComponentInChildren<Animator>();
                    Require(animator && animator.isHuman && animator.avatar.isValid,"Existing humanoid rig animates");
                    Require(animator.GetCurrentAnimatorStateInfo(0).normalizedTime>0,"Existing walking controller advanced");
                    var regions=gaze.avatarRegions;
                    Require(regions && regions.Colliders.Count>=9,"Animated body proxies generated");
                    var labels=new HashSet<BodyRegion>(regions.Colliders.Select(c=>c.GetComponent<AvatarBodyRegion>().region));
                    Require(labels.Count==9,"All nine requested anatomical regions available");
                    Physics.SyncTransforms();
                    var head=regions.Colliders.First(c=>c.GetComponent<AvatarBodyRegion>().region==BodyRegion.Head);
                    Ray ray=new Ray(gaze.participantCamera.transform.position,(head.bounds.center-gaze.participantCamera.transform.position).normalized);
                    Require(regions.Raycast(ray,30,out var hit,out var region) && region.region==BodyRegion.Head,"Gaze ray intersects avatar head proxy");
                    Require(gaze.Current.simulated && gaze.Current.valid,"Explicit synthetic gaze resolved");
                    Require(Vector3.Angle(gaze.Current.ray.direction,gaze.participantCamera.transform.forward)>10,"Eye offset changes direction while head stationary");
                    Require(platform.logger.Recording && platform.logger.RowsWritten>2,"CSV received frame samples");
                    Require(gaze.rpField.overlay.overlayMaterial.GetFloat("_BlurAmount")>=3.599f && gaze.rpField.overlay.overlayMaterial.GetFloat("_BlurAmount")<=3.601f,"Active blur is reduced by forty percent (6 to 3.6)");
                    CheckCsv();
                    gaze.rpField.centerMode=RpFieldDriver.CenterMode.HeadCentered;
                    phaseAt=Time.realtimeSinceStartupAsDouble;
                    SessionState.SetInt(StateKey,2);
                }
                else if(state==2 && Time.realtimeSinceStartupAsDouble-phaseAt>.5)
                {
                    Require(!gaze.rpField.UsingGaze,"Head-centered switch ignores eye offset");
                    Require(Vector3.Distance(initialPosition,platform.AvatarInstance.position)>.1f,"Existing movement driver moves walking avatar");
                    Capture(gaze.participantCamera,"IntegrationEvidence/editor-head-centered.png");
                    gaze.rpField.centerMode=RpFieldDriver.CenterMode.GazeCentered;
                    phaseAt=Time.realtimeSinceStartupAsDouble;
                    SessionState.SetInt(StateKey,3);
                }
                else if(state==3 && Time.realtimeSinceStartupAsDouble-phaseAt>1)
                {
                    Require(gaze.rpField.UsingGaze,"Gaze-centered switch consumes eye offset");
                    Require(gaze.Current.hit && gaze.Current.bodyRegion=="Head","Full gaze-to-anatomy pipeline resolves the animated head");
                    Capture(gaze.participantCamera,"IntegrationEvidence/editor-gaze-centered.png");
                    CheckRealValidity(gaze);
                    // Restore synthetic mode for the remaining frame, then close the log.
                    gaze.useSyntheticGaze=true;
                    platform.logger.RecordResponse("pilot,\"test\"");
                    platform.logger.StopRecording();
                    Require(File.ReadAllLines(platform.logger.LogPath).Any(l=>l.Contains("response")),"Response event persisted in session CSV");
                    File.Copy(platform.logger.LogPath,"IntegrationEvidence/editor-validation.csv",true);
                    phaseAt=Time.realtimeSinceStartupAsDouble;
                    SessionState.SetInt(StateKey,4);
                }
                else if(state==4 && Time.realtimeSinceStartupAsDouble-phaseAt>1)
                {
                    File.WriteAllLines("IntegrationEvidence/platform-checks.txt",findings.Concat(new[]{"These synthetic editor checks do not verify on-device latency, calibration accuracy or stereo rendering."}));
                    Debug.Log("INTEGRATION_PLATFORM_CHECKS_PASS");
                    SessionState.SetInt(StateKey,5);
                    EditorApplication.isPlaying=false;
                }
            }
            catch(Exception e)
            {
                File.WriteAllLines("IntegrationEvidence/platform-checks.txt",findings.Concat(new[]{"FAIL: "+e}));
                Debug.LogException(e);
                SessionState.SetInt(StateKey,0);
                EditorApplication.Exit(1);
            }
        }
        static void CheckSmoothing()
        {
            var filter=new GazeWindowSmoother();
            float sum=0;
            for(int i=0;i<120;i++)
            {
                var input=Quaternion.Euler(0,i%2==0?1:-1,0)*Vector3.forward;
                var output=filter.Step(input,1f/72,2,.03f,8);
                if(i>=30) sum+=Mathf.Pow(Vector3.Angle(output,Vector3.forward),2);
            }
            Require(Mathf.Sqrt(sum/90)<.25f,"Window filter reduces alternating fixation jitter by at least seventy-five percent");
            var target=Quaternion.Euler(-10,20,0)*Vector3.forward;
            Vector3 settled=Vector3.zero;
            for(int i=0;i<36;i++) settled=filter.Step(target,1f/72,2,.03f,8);
            Require(Vector3.Angle(settled,target)<.1f,"Window filter preserves the steady gaze target without reducing gain");
            filter.Reset(); filter.Step(Vector3.forward,1f/72,2,.03f,8);
            for(int i=0;i<12;i++) settled=filter.Step(target,1f/72,2,.03f,8);
            Require(Vector3.Angle(settled,target)<1f,"Window filter follows a large eye movement within 167 milliseconds");
            filter.Reset();
            Require(Vector3.Angle(filter.Step(target,1f/72,2,.03f,8),target)<.01f,"Window filter resets without carrying stale gaze");
        }
        static void CheckPassthrough(NeonGazeBridge gaze)
        {
            var camera=gaze.participantCamera;
            var rig=camera.GetComponentInParent<OVRCameraRig>();
            var layer=rig.GetComponentInChildren<OVRPassthroughLayer>();
            Require(rig.GetComponent<OVRManager>().isInsightPassthroughEnabled && layer && layer.enabled && !layer.hidden && layer.overlayType==OVROverlay.OverlayType.Underlay && layer.textureOpacity==1,"Native passthrough is enabled beneath the avatar");
            Require(camera.backgroundColor.a==0 && !camera.allowHDR && PlayerSettings.preserveFramebufferAlpha,"Transparent camera output preserves the passthrough mask alpha");
            Require(!GameObject.Find("Seated Experiment Floor").GetComponent<Renderer>().enabled,"Virtual floor is hidden so the real room can show");
            var probe=new GameObject("Alpha output probe",typeof(Camera));
            var probeCamera=probe.GetComponent<Camera>();
            probeCamera.enabled=false;
            probeCamera.CopyFrom(camera);
            probeCamera.enabled=false;
            probeCamera.cullingMask=1<<31;
            probe.transform.SetPositionAndRotation(camera.transform.position,camera.transform.rotation);
            var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.layer=31;
            UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.position=probe.transform.position+probe.transform.forward;
            var material=new Material(gaze.rpField.overlay.overlayMaterial);
            quad.GetComponent<Renderer>().sharedMaterial=material;
            quad.GetComponent<Renderer>().localBounds=new Bounds(Vector3.zero,Vector3.one*100);
            material.SetVector("_HeadRightWS",probe.transform.right);
            material.SetVector("_HeadUpWS",probe.transform.up);
            material.SetVector("_HeadForwardWS",probe.transform.forward);
            material.SetVector("_GazeDirectionWS",probe.transform.forward);
            // Non-Property shader uniforms are not copied by the Material constructor.
            material.SetFloat("_PlaneDistance",gaze.rpField.planeDistance);
            material.SetFloat("_PlaneWidth",gaze.rpField.planeWidth);
            var target=new RenderTexture(256,256,24,RenderTextureFormat.ARGB32);
            target.Create();
            var pixels=new Texture2D(256,256,TextureFormat.RGBA32,false);
            var previous=RenderTexture.active;
            try
            {
                RenderPipeline.SubmitRenderRequest(probeCamera,new UniversalRenderPipeline.SingleCameraRequest { destination=target });
                RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,256,256),0,0); pixels.Apply();
                var inside=pixels.GetPixel(128,128); var outside=pixels.GetPixel(16,16);
                Require(Mathf.Abs(inside.a-.85f)<.015f,"Tunnel interior alpha leaves the real room visible with the existing RP dimming");
                File.WriteAllBytes("IntegrationEvidence/passthrough-mask-alpha.png",pixels.EncodeToPNG());
                File.WriteAllText("IntegrationEvidence/passthrough-mask-pixels.txt","inside="+inside+"; outside="+outside+"; plane="+material.GetFloat("_PlaneDistance")+"; width="+material.GetFloat("_PlaneWidth")+"; radius="+material.GetFloat("_VignetteRadius"));
                Require(outside.a>.995f && outside.r<.01f && outside.g<.01f && outside.b<.01f,"Tunnel periphery renders opaque black to hide both the room and avatar");
                material.SetInt("_VisionType",0);
                RenderPipeline.SubmitRenderRequest(probeCamera,new UniversalRenderPipeline.SingleCameraRequest { destination=target });
                pixels.ReadPixels(new Rect(0,0,256,256),0,0); pixels.Apply();
                Require(pixels.GetPixel(128,128).a<.005f && pixels.GetPixel(16,16).a<.005f,"Disabling RP restores transparent output across the room");
            }
            finally
            {
                RenderTexture.active=previous;
                target.Release();
                UnityEngine.Object.DestroyImmediate(pixels);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(quad);
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }
        static void CheckTrackingInterruptions(NeonGazeBridge gaze)
        {
            var probe=GameObject.CreatePrimitive(PrimitiveType.Quad);
            var overlay=probe.AddComponent<PassthroughCameraSamples.MultiObjectDetection.LowVisionOverlayController>();
            overlay.overlayMaterial=new Material(gaze.rpField.overlay.overlayMaterial);
            var field=probe.AddComponent<RpFieldDriver>();
            field.participantCamera=gaze.participantCamera; field.overlay=overlay;
            field.centerMode=RpFieldDriver.CenterMode.GazeCentered;
            field.gazeUsable=true;
            field.gazeDirectionWorld=gaze.participantCamera.transform.rotation * Quaternion.Euler(0,20,0)*Vector3.forward;
            var apply=typeof(RpFieldDriver).GetMethod("Apply",BindingFlags.NonPublic|BindingFlags.Instance);
            var lastFrame=typeof(RpFieldDriver).GetField("lastSmoothedFrame",BindingFlags.NonPublic|BindingFlags.Instance);
            var lastTime=typeof(RpFieldDriver).GetField("lastSmoothedTime",BindingFlags.NonPublic|BindingFlags.Instance);
            var lastValid=typeof(RpFieldDriver).GetField("lastUsableGazeTime",BindingFlags.NonPublic|BindingFlags.Instance);
            try
            {
                apply.Invoke(field,null);
                Vector3 previous=field.DisplayHeadDirection;
                field.gazeUsable=false;
                lastFrame.SetValue(field,-1); lastTime.SetValue(field,Time.realtimeSinceStartupAsDouble-1.0/72);
                lastValid.SetValue(field,Time.realtimeSinceStartupAsDouble-.05);
                apply.Invoke(field,null);
                Require(!field.UsingGaze && field.HoldingGaze && field.EffectiveCenterMode=="HeldGaze" && Vector3.Angle(previous,field.DisplayHeadDirection)<.01f,"Brief invalid tracking holds the displayed window without treating gaze as valid");
                lastFrame.SetValue(field,-1); lastTime.SetValue(field,Time.realtimeSinceStartupAsDouble-1.0/72);
                lastValid.SetValue(field,Time.realtimeSinceStartupAsDouble-.2);
                apply.Invoke(field,null);
                Require(!field.HoldingGaze && field.EffectiveCenterMode=="HeadCenteredTransition" && Vector3.Angle(field.DisplayHeadDirection,Vector3.forward)>1 && Vector3.Angle(previous,field.DisplayHeadDirection)<10,"Sustained tracking loss eases toward head center without snapping");
                previous=field.DisplayHeadDirection;
                field.gazeUsable=true;
                lastFrame.SetValue(field,-1); lastTime.SetValue(field,Time.realtimeSinceStartupAsDouble-1.0/72);
                apply.Invoke(field,null);
                Require(field.UsingGaze && !field.HoldingGaze && Vector3.Angle(previous,field.DisplayHeadDirection)<5,"Tracking recovery continues the filter without resetting to the raw ray");
                field.gazeUsable=false;
                for(int i=0;i<45;i++)
                {
                    lastFrame.SetValue(field,-1); lastTime.SetValue(field,Time.realtimeSinceStartupAsDouble-1.0/72);
                    lastValid.SetValue(field,Time.realtimeSinceStartupAsDouble-.2);
                    apply.Invoke(field,null);
                }
                previous=field.DisplayHeadDirection;
                field.gazeUsable=true;
                lastFrame.SetValue(field,-1); lastTime.SetValue(field,Time.realtimeSinceStartupAsDouble-1.0/72);
                apply.Invoke(field,null);
                Require(Vector3.Angle(previous,Vector3.forward)<1 && Vector3.Angle(previous,field.DisplayHeadDirection)<5,"Recovery after long tracking loss starts gently instead of accelerating toward the raw target");
                field.centerMode=RpFieldDriver.CenterMode.HeadCentered;
                apply.Invoke(field,null);
                Require(field.EffectiveCenterMode=="HeadCentered" && Vector3.Angle(field.DisplayHeadDirection,Vector3.forward)<.01f,"Explicit head-centered mode remains centered immediately");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(overlay.overlayMaterial);
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }
        static void CheckCsv()
        {
            string path="IntegrationEvidence/csv-escaping-check.csv";
            if(File.Exists(path)) File.Delete(path);
            using(var writer=new ResearchCsvWriter(path,new[]{"text","number"}))
                writer.Write(new Dictionary<string,string>{{"text","participant,\"quoted\"\nשלום"},{"number","-0.123"}});
            string output=File.ReadAllText(path);
            Require(output.Contains("\"participant,\"\"quoted\"\"\nשלום\",-0.123"),"CSV escapes quotes/newlines and preserves Unicode/numeric sign");
            Require(ResearchSessionLogger.Columns.Distinct().Count()==ResearchSessionLogger.Columns.Length,"CSV column names are unique");
        }
        static void CheckRealValidity(NeonGazeBridge gaze)
        {
            // Exercise the actual validity gate with official sensor intrinsics and a controlled snapshot.
            var storage=gaze.storage;
            JsonUtility.FromJsonOverwrite(File.ReadAllText("Packages/com.pupil-labs.neon-xr.core/Runtime/Addressables/config.json"),storage.Config);
            DataUtils.ParseCalibrationBytes(File.ReadAllBytes("Packages/com.pupil-labs.neon-xr.core/Runtime/Addressables/calibration.bytes"),storage.CameraIntrinsics);
            typeof(DataStorage).GetProperty("Ready").SetValue(storage,true);
            var offset=storage.Config.sensorCalibration.offset;
            gaze.provider.SetGazeOrigin(offset.position.ToVector3(),offset.rotation.ToVector3());
            Vector2 point=new Vector2(storage.CameraIntrinsics.cameraMatrix[2],storage.CameraIntrinsics.cameraMatrix[5]);
            var f=new GazeFrame { hasSample=true, sequence=1000, raw=new GazeData { type=EtDataType.GazeData, gazePoint=point, worn=true }, receivedRealtime=Time.realtimeSinceStartupAsDouble };
            gaze.useSyntheticGaze=false;
            var latest=typeof(NeonGazeBridge).GetField("latest",BindingFlags.NonPublic|BindingFlags.Instance);
            var resolve=typeof(NeonGazeBridge).GetMethod("Resolve",BindingFlags.NonPublic|BindingFlags.Instance);
            latest.SetValue(gaze,f);
            gaze.mountCalibrationVerified=false;
            var result=(GazeFrame)resolve.Invoke(gaze,null);
            Require(!result.valid && result.invalidReason=="mount_calibration_unverified","Unverified mount calibration rejects gaze");
            gaze.mountCalibrationVerified=true;
            result=(GazeFrame)resolve.Invoke(gaze,null);
            Require(result.valid && NeonGazeBridge.FiniteDirection(result.ray.direction),"Official intrinsics plus mount pose map a fresh sample to world ray");
            f.receivedRealtime-=10; latest.SetValue(gaze,f);
            result=(GazeFrame)resolve.Invoke(gaze,null);
            Require(!result.valid && result.invalidReason=="stale_sample","Stale gaze is rejected");
            f.receivedRealtime=Time.realtimeSinceStartupAsDouble; f.raw.worn=false; latest.SetValue(gaze,f);
            result=(GazeFrame)resolve.Invoke(gaze,null);
            Require(!result.valid && result.invalidReason=="not_worn","Not-worn gaze is rejected");
            f.raw.worn=true; f.raw.gazePoint=new Vector2(float.NaN,0); latest.SetValue(gaze,f);
            result=(GazeFrame)resolve.Invoke(gaze,null);
            Require(!result.valid && result.invalidReason=="invalid_direction","Nonfinite gaze is rejected");
            Require(!NeonGazeBridge.FiniteDirection(Vector3.zero),"Zero gaze direction is rejected");
            f.simulated=true;
            f.raw.type=EtDataType.EyeStateEyelidGazeData;
            f.raw.eyeState.pupilDiameterLeft=.0035f; f.raw.eyeState.pupilDiameterRight=.004f;
            f.raw.eyeState.eyeballCenterLeft=new Vector3(-.032f,-.02f,.03f);
            f.raw.eyelid.eyelidAngleTopLeft=90; f.raw.eyelid.eyelidApertureLeft=5;
            var logger=UnityEngine.Object.FindFirstObjectByType<ResearchSessionLogger>();
            typeof(ResearchSessionLogger).GetMethod("Write",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(logger,new object[]{"sdk_unit_fixture",f});
            typeof(ResearchSessionLogger).GetMethod("Flush",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(logger,null);
            string content;
            using(var reader=new StreamReader(new FileStream(logger.LogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))) content=reader.ReadToEnd();
            string[] cells=content.Split('\n').First(l=>l.StartsWith("3,sdk_unit_fixture,")).Split(',');
            string[] columns=ResearchSessionLogger.Columns;
            double pupil=double.Parse(cells[Array.IndexOf(columns,"pupil_left_mm")],CultureInfo.InvariantCulture);
            double center=double.Parse(cells[Array.IndexOf(columns,"sensor_left_eyeball_center_m_x")],CultureInfo.InvariantCulture);
            double angle=double.Parse(cells[Array.IndexOf(columns,"eyelid_top_left_rad")],CultureInfo.InvariantCulture);
            double aperture=double.Parse(cells[Array.IndexOf(columns,"eyelid_aperture_left_mm")],CultureInfo.InvariantCulture);
            Require(Math.Abs(pupil-3.5)<.00001 && Math.Abs(center+.032)<.00001 && Math.Abs(angle-Math.PI/2)<.00001 && aperture==5,"CSV converts SDK pupil/angle units and preserves center/aperture units");
        }

        static void Capture(Camera camera,string path)
        {
            var target=new RenderTexture(1000,1000,24,RenderTextureFormat.ARGB32);
            target.Create();
            var request=new UniversalRenderPipeline.SingleCameraRequest { destination=target };
            RenderPipeline.SubmitRenderRequest(camera,request);
            RenderTexture previous=RenderTexture.active;
            RenderTexture.active=target;
            var pixels=new Texture2D(1000,1000,TextureFormat.RGB24,false);
            pixels.ReadPixels(new Rect(0,0,1000,1000),0,0); pixels.Apply();
            File.WriteAllBytes(path,pixels.EncodeToPNG());
            RenderTexture.active=previous;
            UnityEngine.Object.DestroyImmediate(pixels); target.Release(); UnityEngine.Object.DestroyImmediate(target);
            Require(File.Exists(path) && new FileInfo(path).Length>1000,"Rendered mono preview saved: "+Path.GetFileName(path));
        }
    }
}
