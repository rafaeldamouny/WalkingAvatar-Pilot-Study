using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using CommonUsages = UnityEngine.XR.CommonUsages;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace WalkingEyeExperiment
{
    [DefaultExecutionOrder(-1000)]
    public class SeatedWalkingPlatform : MonoBehaviour
    {
        public enum GazeSource { HeadsetOnly, PupilLabs }
        public GameObject avatarPrefab;
        public Avatar avatarRigAsset;
        public RuntimeAnimatorController animationController;
        public AnimationClip kevinWalkClip;
        public Camera participantCamera;
        public NeonGazeBridge gaze;
        public ResearchSessionLogger logger;
        public Text headsetStatus;
        public float initialDistance = 2;
        public float pathLength = 6;
        public float seatedEyeHeight = 1.2f;
        public float walkingSpeed = 1;
        // Retained only for the legacy in-place avatar helper; the experiment uses kevinWalkClip directly.
        public float kevinWalkReferenceSpeed = 2f;
        public float kevinControllerWalkMultiplier = 0.6f;
        public bool autoWalk = false;
        public bool showStatus = true;
        public bool passthroughRoom = true;
        [Header("Experiment start menu")]
        public bool requireStartMenu = true;
        public bool requireValidGazeToStart = true;
        public GazeSource selectedGazeSource = GazeSource.HeadsetOnly;
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
        TMPro.TextMeshProUGUI menuParticipantText, menuTunnelText, menuStatusText, menuAvatarText, menuSimulationText, menuGazeSourceText, menuRunModeText, menuRunModeHelpText, menuTrialSummaryText;
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
            device.TryGetFeatureValue(CommonUsages.primary2DAxis,out Vector2 axis);
            leftDevice.TryGetFeatureValue(CommonUsages.primaryButton,out bool leftPrimary);
            leftDevice.TryGetFeatureValue(CommonUsages.secondaryButton,out bool leftSecondary);
            bool startKey=Keyboard.current!=null && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.aKey.wasPressedThisFrame);
            if(!ExperimentStarted)
            {
                UpdateStartMenu(axis);
                if(secondary && !secondaryWasDown) SetTunnelFov(selectedTunnelFov==15 ? 30 : 15);
                bool simulationKey=Keyboard.current!=null && Keyboard.current.sKey.wasPressedThisFrame;
                if((leftPrimary && !leftPrimaryWasDown) || simulationKey) SetSimulationEnabled(!simulationEnabled);
                bool gazeSourceKey=Keyboard.current!=null && Keyboard.current.tKey.wasPressedThisFrame;
                if((leftSecondary && !leftSecondaryWasDown) || gazeSourceKey)
                    SetGazeSource(selectedGazeSource==GazeSource.HeadsetOnly ? GazeSource.PupilLabs : GazeSource.HeadsetOnly);
                if((primary && !primaryWasDown) || startKey) BeginExperiment();
                primaryWasDown=primary; secondaryWasDown=secondary; leftPrimaryWasDown=leftPrimary; leftSecondaryWasDown=leftSecondary;
                return;
            }
            if(!pilotRunner) return;
            // Mid-session reset needs Left Y held for 1.5 s; after completion one press returns to the menu.
            bool resetPilotKey=Keyboard.current!=null && Keyboard.current.backspaceKey.wasPressedThisFrame;
            if(leftSecondary && !leftSecondaryWasDown) leftYHeldSince=Time.unscaledTime;
            bool heldLongEnough=leftSecondary && Time.unscaledTime-leftYHeldSince>=1.5f;
            bool sessionDone=!pilotRunner.IsRunning;
            if(resetPilotKey || (sessionDone && leftSecondary && !leftSecondaryWasDown) || (!sessionDone && heldLongEnough)) ResetExperiment();
            primaryWasDown=primary; secondaryWasDown=secondary;
            leftPrimaryWasDown=leftPrimary; leftSecondaryWasDown=leftSecondary;
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
            menuAvatarText.text="MOTION     Kevin walk animation · same motion in avatar, biological motion and sticks";
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
            ResolveKevinWalkClip();
            if(!kevinWalkClip)
            {
                if(menuStatusText) { menuStatusText.color=Color.red; menuStatusText.text="Kevin walk clip is not assigned in the scene."; }
                Debug.LogError("[SeatedPlatform] Assign Kevin's HumanM@Walk01_Forward animation clip before starting the experiment.",this);
                return;
            }
            BeginPerceptionPilotDemo();
        }

        void ResolveKevinWalkClip()
        {
            if (kevinWalkClip) return;
#if UNITY_EDITOR
            // Unity can keep the old scene object alive after its external-reload prompt is dismissed.
            // Load the exact FBX clip so the current Play Mode session can still use Kevin's motion.
            const string path = "Assets/Kevin Iglesias/Human Animations/Animations/Male/Movement/Walk/HumanM@Walk01_Forward.fbx";
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is AnimationClip clip && clip.name == "HumanM@Walk01_Forward")
                {
                    kevinWalkClip = clip;
                    Debug.Log("[SeatedPlatform] Loaded Kevin's walk clip for this Play Mode session.", this);
                    return;
                }
#endif
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
            pilotRunner.Configure(avatarPrefab,avatarRigAsset,kevinWalkClip,participantCamera,head,rightHand,
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
            // Menu draws in queue 5000 (after the tunnel overlay at 4900). Never use >5000: URP only renders transparent queues 2501-5000.
            // TMP SDF text: crisp in world space; its font material is cloned and drawn in the same overlay queue as the cards.
            var panel=NewUi<Image>(root.transform,"Panel",Vector2.zero,new Vector2(960,1180));
            panel.color=new Color(.025f,.045f,.075f,.98f); panel.material=menuMaterial;
            var accent=NewUi<Image>(root.transform,"Header Accent",new Vector2(0,565),new Vector2(860,7));
            accent.color=new Color(.20f,.78f,.92f,1f); accent.material=menuMaterial;
            var title=NewText(root.transform,"Title",new Vector2(0,510),new Vector2(850,72),"WALKING STUDY",46,FontStyle.Bold);
            title.color=new Color(.92f,.97f,1f,1f);
            var subtitle=NewText(root.transform,"Subtitle",new Vector2(0,458),new Vector2(850,42),"Start the full perception experiment",22,FontStyle.Normal);
            subtitle.color=new Color(.68f,.80f,.89f,1f);

            AddMenuCard(root.transform,"Run Mode Card",340,125,new Color(.07f,.14f,.21f,.98f));
            menuRunModeText=NewText(root.transform,"Run Mode",new Vector2(0,365),new Vector2(850,50),"",30,FontStyle.Bold);
            menuRunModeText.color=new Color(.42f,.89f,1f,1f);
            menuRunModeHelpText=NewText(root.transform,"Run Mode Help",new Vector2(0,315),new Vector2(850,42),"",20,FontStyle.Normal);

            AddMenuCard(root.transform,"Avatar Card",230,76,new Color(.055f,.105f,.16f,.98f));
            menuAvatarText=NewText(root.transform,"Motion and appearances",new Vector2(0,230),new Vector2(850,60),"",23,FontStyle.Bold);

            AddMenuCard(root.transform,"Participant Card",135,64,new Color(.055f,.105f,.16f,.98f));
            menuParticipantText=NewText(root.transform,"Participant",new Vector2(0,135),new Vector2(850,54),"",23,FontStyle.Bold);
            menuParticipantText.alignment=TMPro.TextAlignmentOptions.Left;

            AddMenuCard(root.transform,"Simulation Card",55,64,new Color(.055f,.105f,.16f,.98f));
            menuSimulationText=NewText(root.transform,"Simulation",new Vector2(0,55),new Vector2(850,54),"",23,FontStyle.Bold);
            menuSimulationText.alignment=TMPro.TextAlignmentOptions.Left;

            AddMenuCard(root.transform,"Tunnel Card",-25,64,new Color(.055f,.105f,.16f,.98f));
            menuTunnelText=NewText(root.transform,"Tunnel",new Vector2(0,-25),new Vector2(850,54),"",23,FontStyle.Bold);
            menuTunnelText.alignment=TMPro.TextAlignmentOptions.Left;

            AddMenuCard(root.transform,"Eye Tracking Card",-105,64,new Color(.055f,.105f,.16f,.98f));
            menuGazeSourceText=NewText(root.transform,"Eye Tracking",new Vector2(0,-105),new Vector2(850,54),"",23,FontStyle.Bold);
            menuGazeSourceText.alignment=TMPro.TextAlignmentOptions.Left;

            menuStatusText=NewText(root.transform,"Status",new Vector2(0,-190),new Vector2(850,50),"",21,FontStyle.Bold);
            menuStatusText.color=new Color(.55f,1f,.75f,1f);
            menuTrialSummaryText=NewText(root.transform,"Trial Summary",new Vector2(0,-270),new Vector2(850,95),"",18,FontStyle.Normal);
            menuTrialSummaryText.color=new Color(.78f,.86f,.92f,1f);
            AddMenuCard(root.transform,"Controls Card",-445,190,new Color(.045f,.08f,.12f,.98f));
            var controlsTitle=NewText(root.transform,"Controls Title",new Vector2(0,-385),new Vector2(820,42),"CONTROLS",20,FontStyle.Bold);
            controlsTitle.color=new Color(.42f,.89f,1f,1f);
            var help=NewText(root.transform,"Controls",new Vector2(0,-455),new Vector2(850,120),
                "RIGHT STICK: participant number\nX: simulation     ·     B: tunnel width     ·     Y: eye tracking\nA: start     ·     TRIGGER: calibrate / continue / point     ·     A/B: stairs     ·     LEFT Y (hold 1.5 s): reset",17,FontStyle.Normal);
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
            if(menuRunModeText) menuRunModeText.text="FULL EXPERIMENT · KEVIN WALK";
            // Counts come from the recorded clips that exist; maneuvers without a verified clip are left out.
            var design=PerceptionPilotDemoRunner.DescribeDesign().Split('|');
            if(menuRunModeHelpText) menuRunModeHelpText.text=design[0]+" trials · intro: biological motion → avatar → stick figure";
            if(menuTrialSummaryText) menuTrialSummaryText.text="Kevin's walk animates all three appearances. Recorded paths set the maneuver and timing.\nRecorded: "+design[4]+(design[5].Length>0 ? "\nNot recorded yet (left out): "+design[5] : "")+
                "\n3 cutoffs × 2 repeats × 6 conditions = "+design[1]+" test + "+design[2]+" catch + "+design[3]+" practice";
            UpdateAvatarLabel();
        }
        T NewUi<T>(Transform parent,string objectName,Vector2 position,Vector2 size) where T:Graphic
        {
            var go=new GameObject(objectName,typeof(RectTransform),typeof(T)); go.transform.SetParent(parent,false);
            var rt=go.GetComponent<RectTransform>(); rt.anchorMin=rt.anchorMax=new Vector2(.5f,.5f); rt.anchoredPosition=position; rt.sizeDelta=size;
            return go.GetComponent<T>();
        }
        TMPro.TextMeshProUGUI NewText(Transform parent,string objectName,Vector2 position,Vector2 size,string value,int fontSize,FontStyle style)
        {
            var text=NewUi<TMPro.TextMeshProUGUI>(parent,objectName,position,size);
            if(!text.font) text.font=TMPro.TMP_Settings.defaultFontAsset;
            if(!menuTextMaterial && text.font) { menuTextMaterial=new Material(text.font.material); menuTextMaterial.renderQueue=5000; }
            if(menuTextMaterial) text.fontSharedMaterial=menuTextMaterial;
            text.fontSize=fontSize; text.fontStyle=style==FontStyle.Bold ? TMPro.FontStyles.Bold : TMPro.FontStyles.Normal;
            text.color=Color.white; text.alignment=TMPro.TextAlignmentOptions.Center; text.enableWordWrapping=true;
            text.overflowMode=TMPro.TextOverflowModes.Overflow; text.margin=new Vector4(24,0,24,0); text.raycastTarget=false; text.text=value; return text;
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
            // This Kevin clip is in-place, so SimpleWalkDriver moves the root. Scale the
            // animation playback to that same speed to avoid foot sliding from a cadence
            // that assumes a faster walk. The current controller state is authored at 0.6x.
            float authoredWorldSpeed=kevinWalkReferenceSpeed*Mathf.Max(.01f,kevinControllerWalkMultiplier);
            animator.speed=walkingSpeed/Mathf.Max(.01f,authoredWorldSpeed);
            Debug.Log($"[SeatedPlatform] Kevin walk sync: path={walkingSpeed:0.00} m/s, source={authoredWorldSpeed:0.00} m/s, animator playback={animator.speed:0.000}x",this);
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
