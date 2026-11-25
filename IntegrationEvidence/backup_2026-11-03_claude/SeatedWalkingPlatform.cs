using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace WalkingEyeExperiment
{
    [DefaultExecutionOrder(-1000)]
    public class SeatedWalkingPlatform : MonoBehaviour
    {
        public enum GazeSource { HeadsetOnly, PupilLabs }
        public enum RunMode { WalkingPreview, PerceptionPilotDemo }
        public GameObject avatarPrefab;
        public Avatar avatarRigAsset;
        public RuntimeAnimatorController animationController;
        public Camera participantCamera;
        public NeonGazeBridge gaze;
        public ResearchSessionLogger logger;
        public Text headsetStatus;
        public float initialDistance = 2;
        public float pathLength = 6;
        public float seatedEyeHeight = 1.2f;
        public float walkingSpeed = 1;
        public bool autoWalk = false;
        public bool showStatus = true;
        public bool passthroughRoom = true;
        [Header("Experiment start menu")]
        public bool requireStartMenu = true;
        public bool requireValidGazeToStart = true;
        public GazeSource selectedGazeSource = GazeSource.HeadsetOnly;
        public RunMode selectedRunMode = RunMode.PerceptionPilotDemo;
        [Range(1,9999)] public int participantNumber = 1;
        public int selectedTunnelFov = 15;
        public AvatarAppearanceController.Appearance selectedAppearance = AvatarAppearanceController.Appearance.Realistic;
        public bool simulationEnabled = true;
        public bool startWalkingOnExperimentStart = true;
        public bool allowRuntimeControlsDuringExperiment = false;
        public bool ExperimentStarted { get; private set; }
        public Transform AvatarInstance { get; private set; }
        SimpleWalkDriver driver;
        bool started, walkStarted, primaryWasDown, secondaryWasDown, stickWasDown, leftPrimaryWasDown, leftSecondaryWasDown, reachedGoal;
        bool axisLatched, leftStickWasDown;
        float readyAt;
        float leftYHeldSince;
        int repeatIndex;
        float nextStatus;
        Canvas startMenuCanvas;
        Text menuParticipantText, menuTunnelText, menuStatusText, menuAvatarText, menuSimulationText, menuGazeSourceText, menuRunModeText, menuRunModeHelpText, menuTrialSummaryText;
        Material menuMaterial, menuTextMaterial;
        PerceptionPilotDemoRunner pilotRunner;

#if UNITY_EDITOR
        void OnEnable() { Application.onBeforeRender+=PreviewEditorHead; }
        void OnDisable() { Application.onBeforeRender-=PreviewEditorHead; }
        [BeforeRenderOrder(50)]
        void PreviewEditorHead()
        {
            if(participantCamera && !XRSettings.isDeviceActive)
                participantCamera.transform.localPosition=new Vector3(0,seatedEyeHeight,0);
        }
#endif

        void Awake()
        {
            // Deactivate the complete Neon prefab before its components start discovery.
            SetGazeSource(selectedGazeSource);
        }

        void Start()
        {
            // Standard alpha-over blending over transparent black produces premultiplied
            // framebuffer RGB. Match the compositor to that representation.
            if(passthroughRoom && XRSettings.isDeviceActive)
                OVRManager.eyeFovPremultipliedAlphaModeEnabled=true;
            readyAt=Time.unscaledTime+.75f;
            if(!avatarPrefab || !participantCamera) { Debug.LogError("[SeatedPlatform] Existing avatar and participant camera required",this); enabled=false; }
            SetGazeSource(selectedGazeSource);
            if(requireStartMenu)
            {
                ExperimentStarted=false;
                BuildStartMenu();
                if(headsetStatus) headsetStatus.gameObject.SetActive(false);
            }
            else ExperimentStarted=true;
        }
        void Update()
        {
#if UNITY_EDITOR
            PreviewEditorHead();
#endif
            var device=InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            var leftDevice=InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            device.TryGetFeatureValue(CommonUsages.primaryButton,out bool primary);
            // OpenXR may expose Touch controller buttons through the Input System device
            // path even when the legacy XR feature query reports no button value.
            var inputSystemRight=XRController.rightHand;
            var inputSystemPrimary=inputSystemRight!=null ? inputSystemRight.TryGetChildControl<ButtonControl>("primaryButton") : null;
            if(inputSystemPrimary!=null)
                primary|=inputSystemPrimary.isPressed;
            device.TryGetFeatureValue(CommonUsages.secondaryButton,out bool secondary);
            device.TryGetFeatureValue(CommonUsages.primary2DAxisClick,out bool stick);
            device.TryGetFeatureValue(CommonUsages.primary2DAxis,out Vector2 axis);
            leftDevice.TryGetFeatureValue(CommonUsages.primaryButton,out bool leftPrimary);
            leftDevice.TryGetFeatureValue(CommonUsages.secondaryButton,out bool leftSecondary);
            leftDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick,out bool leftStick);
            bool walkKey=Keyboard.current!=null && Keyboard.current.spaceKey.wasPressedThisFrame;
            bool centerKey=Keyboard.current!=null && Keyboard.current.gKey.wasPressedThisFrame;
            bool debugKey=Keyboard.current!=null && Keyboard.current.vKey.wasPressedThisFrame;
            bool avatarKey=Keyboard.current!=null && Keyboard.current.cKey.wasPressedThisFrame;
            bool startKey=Keyboard.current!=null && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.aKey.wasPressedThisFrame);
            if(!ExperimentStarted)
            {
                UpdateStartMenu(axis);
                if(secondary && !secondaryWasDown) SetTunnelFov(selectedTunnelFov==15 ? 30 : 15);
                if((stick && !stickWasDown) || avatarKey) CycleAvatarAppearance();
                bool simulationKey=Keyboard.current!=null && Keyboard.current.sKey.wasPressedThisFrame;
                if((leftPrimary && !leftPrimaryWasDown) || simulationKey) SetSimulationEnabled(!simulationEnabled);
                bool gazeSourceKey=Keyboard.current!=null && Keyboard.current.tKey.wasPressedThisFrame;
                if((leftSecondary && !leftSecondaryWasDown) || gazeSourceKey)
                    SetGazeSource(selectedGazeSource==GazeSource.HeadsetOnly ? GazeSource.PupilLabs : GazeSource.HeadsetOnly);
                bool modeKey=Keyboard.current!=null && Keyboard.current.mKey.wasPressedThisFrame;
                if((leftStick && !leftStickWasDown) || modeKey)
                {
                    selectedRunMode=selectedRunMode==RunMode.WalkingPreview ? RunMode.PerceptionPilotDemo : RunMode.WalkingPreview;
                    UpdateRunModeLabel();
                }
                if((primary && !primaryWasDown) || startKey) BeginExperiment();
                primaryWasDown=primary; secondaryWasDown=secondary; stickWasDown=stick; leftPrimaryWasDown=leftPrimary; leftSecondaryWasDown=leftSecondary; leftStickWasDown=leftStick;
                return;
            }
            if(pilotRunner)
            {
                // Mid-session reset needs Left Y held for 1.5 s (a single press could wipe a session). After the session ends, one press is enough.
                bool resetPilotKey=Keyboard.current!=null && Keyboard.current.tKey.wasPressedThisFrame;
                if(leftSecondary && !leftSecondaryWasDown) leftYHeldSince=Time.unscaledTime;
                bool heldLongEnough=leftSecondary && Time.unscaledTime-leftYHeldSince>=1.5f;
                bool sessionDone=!pilotRunner.IsRunning;
                if(resetPilotKey || (sessionDone && leftSecondary && !leftSecondaryWasDown) || (!sessionDone && heldLongEnough)) ResetExperiment();
                primaryWasDown=primary; secondaryWasDown=secondary; stickWasDown=stick;
                leftPrimaryWasDown=leftPrimary; leftSecondaryWasDown=leftSecondary; leftStickWasDown=leftStick;
                return;
            }
            if((stick && !stickWasDown) || (secondary && !secondaryWasDown) || avatarKey) CycleAvatarAppearance();
            bool resetKey=Keyboard.current!=null && Keyboard.current.tKey.wasPressedThisFrame;
            if((leftSecondary && !leftSecondaryWasDown) || resetKey)
            {
                ResetExperiment();
                primaryWasDown=primary; secondaryWasDown=secondary; stickWasDown=stick;
                leftPrimaryWasDown=leftPrimary; leftSecondaryWasDown=leftSecondary; leftStickWasDown=leftStick;
                return;
            }
            if(!started && Time.unscaledTime>=readyAt)
            {
                CreateAvatar(); started=true;
            }
            bool toggleSimulationKey=Keyboard.current!=null && Keyboard.current.sKey.wasPressedThisFrame;
            if((leftPrimary && !leftPrimaryWasDown) || toggleSimulationKey) SetSimulationEnabled(!simulationEnabled);
            if(allowRuntimeControlsDuringExperiment)
            {
                if((primary && !primaryWasDown) || walkKey || startKey) ToggleWalk();
                if((secondary && !secondaryWasDown) || centerKey) ToggleCenter();
                if(debugKey && gaze) gaze.debugVisualization=!gaze.debugVisualization;
                if(Keyboard.current!=null && Keyboard.current.rKey.wasPressedThisFrame) RestartWalk();
            }
            primaryWasDown=primary; secondaryWasDown=secondary; stickWasDown=stick; leftPrimaryWasDown=leftPrimary; leftSecondaryWasDown=leftSecondary; leftStickWasDown=leftStick;
            if(headsetStatus && Time.unscaledTime>=nextStatus)
            {
                headsetStatus.gameObject.SetActive(showStatus);
                string tracking=selectedGazeSource==GazeSource.HeadsetOnly ? "HEADSET ONLY" : "PUPIL LABS";
                headsetStatus.text=tracking+" | "+AppearanceLabel(selectedAppearance)+"\n"+
                    "Right stick click / C: preview avatar  ·  Left Y / T: reset  ·  X / S: tunnel vision";
                nextStatus=Time.unscaledTime+.2f;
            }
        }
        public static string FormatParticipantId(int number) => "P"+Mathf.Clamp(number,1,9999).ToString("D3");
        public void SetParticipantNumber(int number)
        {
            participantNumber=Mathf.Clamp(number,1,9999);
            if(menuParticipantText) menuParticipantText.text="PARTICIPANT     "+FormatParticipantId(participantNumber)+"     ·     change with right stick";
        }
        public void SetTunnelFov(int degrees)
        {
            selectedTunnelFov=degrees==30 ? 30 : 15;
            if(menuTunnelText)
                menuTunnelText.text=$"TUNNEL WIDTH     {selectedTunnelFov}°     ·     press B to change";
        }
        public void SetGazeSource(GazeSource source)
        {
            selectedGazeSource=source;
            bool usePupilLabs=source==GazeSource.PupilLabs;
            requireValidGazeToStart=usePupilLabs;
            if(gaze)
            {
                if(gaze.provider && gaze.provider.gameObject.activeSelf!=usePupilLabs)
                    gaze.provider.gameObject.SetActive(usePupilLabs);
                if(gaze.rpField)
                {
                    gaze.rpField.centerMode=usePupilLabs ? RpFieldDriver.CenterMode.GazeCentered : RpFieldDriver.CenterMode.HeadCentered;
                    if(!usePupilLabs) gaze.rpField.gazeUsable=false;
                }
            }
            if(menuGazeSourceText)
                menuGazeSourceText.text="EYE TRACKING     "+(usePupilLabs ? "Pupil Labs" : "Headset only")+"     ·     press Y to change";
            if(menuStatusText)
            {
                menuStatusText.color=usePupilLabs ? new Color(1,.72f,.25f,1) : new Color(.35f,1,.55f,1);
                menuStatusText.text=usePupilLabs ? "PUPIL LABS SELECTED · waiting for valid gaze" : "READY · headset only (Pupil Labs not required)";
            }
        }
        void UpdateStartMenu(Vector2 axis)
        {
            int delta=0;
            if(!axisLatched)
            {
                if(axis.x>.65f) delta=1; else if(axis.x<-.65f) delta=-1;
                else if(axis.y>.65f) delta=10; else if(axis.y<-.65f) delta=-10;
                if(delta!=0) { SetParticipantNumber(participantNumber+delta); axisLatched=true; }
            }
            if(axis.magnitude<.35f) axisLatched=false;
            if(Keyboard.current!=null)
            {
                if(Keyboard.current.rightArrowKey.wasPressedThisFrame) SetParticipantNumber(participantNumber+1);
                if(Keyboard.current.leftArrowKey.wasPressedThisFrame) SetParticipantNumber(participantNumber-1);
                if(Keyboard.current.upArrowKey.wasPressedThisFrame) SetParticipantNumber(participantNumber+10);
                if(Keyboard.current.downArrowKey.wasPressedThisFrame) SetParticipantNumber(participantNumber-10);
            }
            if(menuStatusText)
            {
                if(selectedGazeSource==GazeSource.HeadsetOnly)
                {
                    menuStatusText.color=new Color(.35f,1,.55f,1);
                    menuStatusText.text="READY · headset only (Pupil Labs not required)";
                    return;
                }
                bool ready=gaze && gaze.Current.valid;
                string reason=gaze && !string.IsNullOrEmpty(gaze.Current.invalidReason) ? "\n"+gaze.Current.invalidReason : "";
                menuStatusText.color=ready ? new Color(.35f,1,.55f,1) : new Color(1,.72f,.25f,1);
                menuStatusText.text=ready ? "READY · Pupil Labs gaze valid" : "PUPIL LABS · waiting for valid gaze"+reason;
            }
        }
        void CycleAvatarAppearance()
        {
            selectedAppearance=selectedAppearance==AvatarAppearanceController.Appearance.Realistic
                ? AvatarAppearanceController.Appearance.BiologicalMotion
                : AvatarAppearanceController.Appearance.Realistic;
            UpdateAvatarLabel();
            if(AvatarInstance)
            {
                var appearance=AvatarInstance.GetComponent<AvatarAppearanceController>();
                if(appearance) appearance.SetAppearance(selectedAppearance);
            }
        }
        public void SetSimulationEnabled(bool enabled)
        {
            simulationEnabled=enabled;
            if(menuSimulationText)
                menuSimulationText.text="TUNNEL SIMULATION     "+(simulationEnabled ? "ON" : "OFF")+"     ·     press X to toggle";
            var overlay=gaze && gaze.rpField ? gaze.rpField.overlay : null;
            if(!overlay) return;
            overlay.ApplySettings(simulationEnabled ? overlay.currentVisionType : PassthroughCameraSamples.MultiObjectDetection.VisionType.None);
        }
        static string AppearanceLabel(AvatarAppearanceController.Appearance appearance)
        {
            switch(appearance)
            {
                case AvatarAppearanceController.Appearance.BiologicalMotion: return "Point-light preview";
                default: return "Realistic avatar";
            }
        }
        void UpdateAvatarLabel()
        {
            if(!menuAvatarText) return;
            menuAvatarText.text=selectedRunMode==RunMode.PerceptionPilotDemo
                ? "AVATAR CONDITIONS     Realistic + Point-light · one at a time"
                : "PREVIEW AVATAR     "+AppearanceLabel(selectedAppearance)+" · right stick click switches";
        }
        public void BeginExperiment()
        {
            if(ExperimentStarted || (requireValidGazeToStart && (!gaze || !gaze.Current.valid))) return;
            SetTunnelFov(selectedTunnelFov);
            if(gaze && gaze.rpField && gaze.rpField.overlay)
            {
                gaze.rpField.overlay.tunnelFovDegrees=selectedTunnelFov;
                gaze.rpField.overlay.ApplyInspectorSettings();
            }
            SetSimulationEnabled(simulationEnabled);
            if(selectedRunMode==RunMode.PerceptionPilotDemo)
            {
                BeginPerceptionPilotDemo();
                return;
            }
            bool recordSession=selectedGazeSource==GazeSource.PupilLabs;
            if(logger && logger.Recording) logger.StopRecording();
            if(recordSession && logger)
            {
                logger.participantId=FormatParticipantId(participantNumber);
                logger.StartRecording();
                if(!logger.Recording)
                {
                    if(menuStatusText) { menuStatusText.color=Color.red; menuStatusText.text="Could not start recording\n"+logger.Error; }
                    return;
                }
            }
            ExperimentStarted=true;
            if(startMenuCanvas) startMenuCanvas.gameObject.SetActive(false);
            showStatus=true;
            if(headsetStatus) headsetStatus.gameObject.SetActive(true);
            if(gaze) gaze.debugVisualization=false;
            CreateAvatar(); started=true;
            if(startWalkingOnExperimentStart) StartNewWalk();
        }
        void BeginPerceptionPilotDemo()
        {
            ExperimentStarted=true;
            if(startMenuCanvas) startMenuCanvas.gameObject.SetActive(false);
            if(headsetStatus) headsetStatus.gameObject.SetActive(false);
            if(gaze) gaze.debugVisualization=false;
            var rig=FindFirstObjectByType<OVRCameraRig>();
            Transform head=rig && rig.centerEyeAnchor ? rig.centerEyeAnchor : participantCamera.transform;
            Transform rightHand=rig ? rig.rightControllerAnchor : null;
            pilotRunner=gameObject.AddComponent<PerceptionPilotDemoRunner>();
            pilotRunner.logGazeSamples=selectedGazeSource==GazeSource.PupilLabs;
            pilotRunner.tunnelSimulationEnabled=simulationEnabled;
            pilotRunner.Configure(avatarPrefab,avatarRigAsset,animationController,participantCamera,head,rightHand,
                gaze && gaze.rpField ? gaze.rpField.overlay : null,logger,participantNumber,seatedEyeHeight);
            pilotRunner.StartSession();
        }
        public void ResetExperiment()
        {
            if(pilotRunner)
            {
                pilotRunner.StopSession();
                Destroy(pilotRunner);
                pilotRunner=null;
            }
            if(logger && logger.Recording)
            {
                logger.EndTrial();
                logger.StopRecording();
            }
            if(driver) driver.StopWalk();
            if(AvatarInstance) Destroy(AvatarInstance.gameObject);
            if(gaze) gaze.avatarRegions=null;
            if(logger) logger.avatar=null;
            AvatarInstance=null;
            driver=null;
            started=false;
            walkStarted=false;
            reachedGoal=false;
            repeatIndex=0;
            ExperimentStarted=false;
            if(headsetStatus) headsetStatus.gameObject.SetActive(false);
            if(startMenuCanvas) startMenuCanvas.gameObject.SetActive(true);
            SetGazeSource(selectedGazeSource);
        }
        void BuildStartMenu()
        {
            var root=new GameObject("Experiment Start Menu",typeof(RectTransform),typeof(Canvas));
            root.transform.SetParent(participantCamera.transform,false);
            root.transform.localPosition=new Vector3(0,0,1.25f);
            root.transform.localRotation=Quaternion.identity;
            root.transform.localScale=Vector3.one*.001f;
            startMenuCanvas=root.GetComponent<Canvas>(); startMenuCanvas.renderMode=RenderMode.WorldSpace;
            startMenuCanvas.worldCamera=participantCamera; startMenuCanvas.overrideSorting=true; startMenuCanvas.sortingOrder=500;
            var rect=root.GetComponent<RectTransform>(); rect.sizeDelta=new Vector2(960,1180);
            menuMaterial=new Material(Shader.Find("UI/Default")); menuMaterial.renderQueue=5000;
            // Text must draw after the cards (5000), which draw after the tunnel overlay (4900); otherwise the cards hide the text.
            menuTextMaterial=new Material(Shader.Find("UI/Default")); menuTextMaterial.renderQueue=5001;
            var panel=NewUi<Image>(root.transform,"Panel",Vector2.zero,new Vector2(960,1180));
            panel.color=new Color(.025f,.045f,.075f,.98f); panel.material=menuMaterial;
            var accent=NewUi<Image>(root.transform,"Header Accent",new Vector2(0,565),new Vector2(860,7));
            accent.color=new Color(.20f,.78f,.92f,1f); accent.material=menuMaterial;
            var title=NewText(root.transform,"Title",new Vector2(0,510),new Vector2(850,72),"WALKING STUDY",46,FontStyle.Bold);
            title.color=new Color(.92f,.97f,1f,1f);
            var subtitle=NewText(root.transform,"Subtitle",new Vector2(0,458),new Vector2(850,42),"Choose a preview or start the full perception experiment",22,FontStyle.Normal);
            subtitle.color=new Color(.68f,.80f,.89f,1f);

            AddMenuCard(root.transform,"Run Mode Card",340,125,new Color(.07f,.14f,.21f,.98f));
            menuRunModeText=NewText(root.transform,"Run Mode",new Vector2(0,365),new Vector2(850,50),"",30,FontStyle.Bold);
            menuRunModeText.color=new Color(.42f,.89f,1f,1f);
            menuRunModeHelpText=NewText(root.transform,"Run Mode Help",new Vector2(0,315),new Vector2(850,42),"",20,FontStyle.Normal);

            AddMenuCard(root.transform,"Avatar Card",230,76,new Color(.055f,.105f,.16f,.98f));
            menuAvatarText=NewText(root.transform,"Preview Avatar",new Vector2(0,230),new Vector2(850,60),"",23,FontStyle.Bold);

            AddMenuCard(root.transform,"Participant Card",135,64,new Color(.055f,.105f,.16f,.98f));
            menuParticipantText=NewText(root.transform,"Participant",new Vector2(0,135),new Vector2(850,54),"",23,FontStyle.Bold);
            menuParticipantText.alignment=TextAnchor.MiddleLeft;

            AddMenuCard(root.transform,"Simulation Card",55,64,new Color(.055f,.105f,.16f,.98f));
            menuSimulationText=NewText(root.transform,"Simulation",new Vector2(0,55),new Vector2(850,54),"",23,FontStyle.Bold);
            menuSimulationText.alignment=TextAnchor.MiddleLeft;

            AddMenuCard(root.transform,"Tunnel Card",-25,64,new Color(.055f,.105f,.16f,.98f));
            menuTunnelText=NewText(root.transform,"Tunnel",new Vector2(0,-25),new Vector2(850,54),"",23,FontStyle.Bold);
            menuTunnelText.alignment=TextAnchor.MiddleLeft;

            AddMenuCard(root.transform,"Eye Tracking Card",-105,64,new Color(.055f,.105f,.16f,.98f));
            menuGazeSourceText=NewText(root.transform,"Eye Tracking",new Vector2(0,-105),new Vector2(850,54),"",23,FontStyle.Bold);
            menuGazeSourceText.alignment=TextAnchor.MiddleLeft;

            menuStatusText=NewText(root.transform,"Status",new Vector2(0,-190),new Vector2(850,50),"",21,FontStyle.Bold);
            menuStatusText.color=new Color(.55f,1f,.75f,1f);
            menuTrialSummaryText=NewText(root.transform,"Trial Summary",new Vector2(0,-270),new Vector2(850,95),"",20,FontStyle.Normal);
            menuTrialSummaryText.color=new Color(.78f,.86f,.92f,1f);
            AddMenuCard(root.transform,"Controls Card",-445,190,new Color(.045f,.08f,.12f,.98f));
            var controlsTitle=NewText(root.transform,"Controls Title",new Vector2(0,-385),new Vector2(820,42),"CONTROLS",20,FontStyle.Bold);
            controlsTitle.color=new Color(.42f,.89f,1f,1f);
            var help=NewText(root.transform,"Controls",new Vector2(0,-455),new Vector2(850,120),
                "LEFT STICK CLICK: preview / full experiment     ·     RIGHT STICK: participant\nRIGHT STICK CLICK: preview avatar     ·     X: simulation     ·     B: tunnel width     ·     Y: eye tracking\nA: start     ·     TRIGGER: calibrate / continue / point     ·     A/B: stairs     ·     LEFT Y (hold 1.5 s): reset",17,FontStyle.Normal);
            SetParticipantNumber(participantNumber);
            SetTunnelFov(selectedTunnelFov);
            SetGazeSource(selectedGazeSource);
            UpdateAvatarLabel();
            SetSimulationEnabled(simulationEnabled);
            UpdateRunModeLabel();
        }
        void AddMenuCard(Transform parent,string objectName,float y,float height,Color color)
        {
            var card=NewUi<Image>(parent,objectName,new Vector2(0,y),new Vector2(880,height));
            card.color=color; card.material=menuMaterial;
        }
        void UpdateRunModeLabel()
        {
            bool full=selectedRunMode==RunMode.PerceptionPilotDemo;
            if(menuRunModeText) menuRunModeText.text=full ? "FULL EXPERIMENT" : "WALK PREVIEW";
            if(menuRunModeHelpText) menuRunModeHelpText.text=full
                ? "241 trials · one avatar at a time · both avatar styles across blocks"
                : "See one walk · change preview avatar with the right stick click";
            if(menuTrialSummaryText) menuTrialSummaryText.text=full
                ? "9 maneuvers: straight · left/right turns (30°, 60°, 90°) · stairs up/down\n3 cutoffs × 2 repeats × 4 conditions = 216 test + 16 catch + 9 practice trials"
                : "The full experiment uses Realistic and Point-Light avatar blocks, each with tunnel simulation on/off.";
            UpdateAvatarLabel();
        }
        T NewUi<T>(Transform parent,string objectName,Vector2 position,Vector2 size) where T:Graphic
        {
            var go=new GameObject(objectName,typeof(RectTransform),typeof(T)); go.transform.SetParent(parent,false);
            var rt=go.GetComponent<RectTransform>(); rt.anchorMin=rt.anchorMax=new Vector2(.5f,.5f); rt.anchoredPosition=position; rt.sizeDelta=size;
            return go.GetComponent<T>();
        }
        Text NewText(Transform parent,string objectName,Vector2 position,Vector2 size,string value,int fontSize,FontStyle style)
        {
            var text=NewUi<Text>(parent,objectName,position,size); text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize=fontSize; text.fontStyle=style; text.color=Color.white; text.alignment=TextAnchor.MiddleCenter;
            text.raycastTarget=false; text.text=value; if(menuTextMaterial) text.material=menuTextMaterial; return text;
        }
        void OnDestroy() { if(menuMaterial) Destroy(menuMaterial); if(menuTextMaterial) Destroy(menuTextMaterial); }
        void CreateAvatar()
        {
            Transform head=participantCamera.transform;
            Vector3 forward=Vector3.ProjectOnPlane(head.forward,Vector3.up).normalized;
            if(forward.sqrMagnitude<.1f) forward=Vector3.forward;
            Vector3 position=head.position+forward*initialDistance;
            // No room scan required. Adjust seatedEyeHeight to the participant's physical eye-to-floor height.
            position.y=head.position.y-seatedEyeHeight;
            var instance=Instantiate(avatarPrefab,position,Quaternion.LookRotation(forward,Vector3.up));
            instance.name="Existing Walking Avatar";
            AvatarInstance=instance.transform;
            foreach(var rb in instance.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic=true; rb.useGravity=false; }
            foreach(var c in instance.GetComponentsInChildren<Collider>(true)) c.enabled=false;
            // Match the original TrialManager: this FBX prefab receives its Animator at spawn.
            var animator=instance.GetComponentInChildren<Animator>() ?? instance.AddComponent<Animator>();
            if(avatarRigAsset) animator.avatar=avatarRigAsset;
            if(animationController) animator.runtimeAnimatorController=animationController;
            animator.applyRootMotion=false;
            animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            var regions=instance.AddComponent<AvatarRegionRig>();
            regions.animator=animator;
            instance.AddComponent<AvatarAppearanceController>().Initialize(animator,selectedAppearance);
            if(gaze) gaze.avatarRegions=regions;
            if(logger) logger.avatar=instance.transform;
            driver=instance.AddComponent<SimpleWalkDriver>();
            driver.MoveSpeed=walkingSpeed;
            driver.LineWidth=0;
            driver.SetPath(new List<Vector3>{position,position+forward*pathLength});
            driver.OnGoalReached+=OnGoal;
            if(autoWalk) StartNewWalk(); else driver.StopWalk();
        }
        void OnGoal()
        {
            reachedGoal=true;
            if(logger && logger.Recording) { logger.EndTrial(); logger.StopRecording(); }
        }
        void StartNewWalk()
        {
            if(!driver) return;
            reachedGoal=false;
            walkStarted=true;
            if(logger && logger.Recording) logger.BeginTrial("platform_"+(++repeatIndex),logger.condition,logger.avatarExemplar);
            driver.StartWalk();
        }
        public void ToggleWalk()
        {
            if(!driver) return;
            if(driver.IsWalking) driver.StopWalk();
            else if(reachedGoal) RestartWalk();
            else if(!walkStarted) StartNewWalk();
            else driver.ResumeWalk();
        }
        public void RestartWalk() { if(driver) { driver.ResetToStart(); StartNewWalk(); } }
        public void ToggleCenter()
        {
            gaze.rpField.centerMode=gaze.rpField.centerMode==RpFieldDriver.CenterMode.HeadCentered ? RpFieldDriver.CenterMode.GazeCentered : RpFieldDriver.CenterMode.HeadCentered;
        }
    }
}
