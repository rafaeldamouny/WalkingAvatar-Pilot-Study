using System.Collections;
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
        public float seatedEyeHeight = 1.65f;
        public float walkingSpeed = 1;
        [Header("Virtual experiment environment")]
        public bool useVirtualCorridorEnvironment = true;
        [Header("Corridor trial alignment")]
        public Transform corridorTJunction;
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
        Canvas validationCanvas;
        TMPro.TextMeshProUGUI validationStatus;
        readonly List<LineRenderer> validationFloorLines = new List<LineRenderer>();
        GameObject validationAvatarRoot;
        GameObject corridorEnvironmentRoot;
        GameObject corridorPreviewAvatar;
        MocapFigure validationFigure;
        MocapClipLibrary.Clip validationClip;
        Vector3[] validationPose, validationWorldPose;
        float validationFloorY;
        string validationFloorSource;
        float validationMotionStarted;
        float validationStartTimeRelT0;
        bool validationActive, validationWalking;
        bool leftStickClickWasDown;

#if UNITY_EDITOR
        void OnEnable() { Application.onBeforeRender+=PreviewEditorHead; }
        void OnDisable() { Application.onBeforeRender-=PreviewEditorHead; }
        [BeforeRenderOrder(50)]
        void PreviewEditorHead()
        {
            // Keep the saved camera object at eye level in the editor even when a Quest is
            // connected but not supplying a live pose to the Scene view.
            if(participantCamera && (!Application.isPlaying || !XRSettings.isDeviceActive))
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
            // Quest floor-level tracking gives the camera a stable eye height above the
            // scene floor. The camera is only sampled during the start calibration; trial
            // placement uses that saved start pose and the corridor junction marker.
            var cameraRig=FindFirstObjectByType<OVRCameraRig>();
            if(cameraRig)
            {
                var manager=cameraRig.GetComponent<OVRManager>();
                if(manager) manager.trackingOriginType=OVRManager.TrackingOrigin.FloorLevel;
            }
            if(XRSettings.isDeviceActive) StartCoroutine(EnsureCameraAboveFloor());
            if (useVirtualCorridorEnvironment)
            {
                // This study runs in a rendered virtual scene, not over the room cameras.
                passthroughRoom = false;
                DisablePassthroughForVirtualScene();
                CreateCorridorPreview();
            }
            // This saved avatar is an editor placement reference only. Never show it in
            // the headset; the live mocap figure is the sole avatar in the app.
            var savedReference=GameObject.Find("Corridor Center Avatar Preview");
            if(savedReference) savedReference.SetActive(false);
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
            if (corridorPreviewAvatar)
            {
                if(corridorPreviewAvatar.activeSelf) corridorPreviewAvatar.SetActive(false);
            }
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
            // Keep the headset's A-button mapping, and make the same actions easy to
            // exercise from Unity Play Mode with the usual desktop start keys.
            bool startKey=Keyboard.current!=null &&
                (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.aKey.wasPressedThisFrame ||
                 Keyboard.current.spaceKey.wasPressedThisFrame);
            bool leftStickClick=leftDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick,out bool lsClick) && lsClick;
            var inputSystemLeft=XRController.leftHand;
            var inputSystemLeftClick=inputSystemLeft!=null ? inputSystemLeft.TryGetChildControl<ButtonControl>("thumbstickClicked") : null;
            if(inputSystemLeftClick!=null) leftStickClick|=inputSystemLeftClick.isPressed;
            bool leftStickPressed=leftStickClick && !leftStickClickWasDown;
            leftStickClickWasDown=leftStickClick;
            if(validationActive)
            {
                UpdateValidation(leftPrimary && !leftPrimaryWasDown, primary && !primaryWasDown,
                    secondary && !secondaryWasDown, leftSecondary && !leftSecondaryWasDown,
                    Keyboard.current!=null && Keyboard.current.xKey.wasPressedThisFrame,
                    Keyboard.current!=null && (Keyboard.current.aKey.wasPressedThisFrame || Keyboard.current.spaceKey.wasPressedThisFrame),
                    Keyboard.current!=null && Keyboard.current.rKey.wasPressedThisFrame,
                    Keyboard.current!=null && Keyboard.current.yKey.wasPressedThisFrame,leftStickPressed);
                primaryWasDown=primary; secondaryWasDown=secondary;
                leftPrimaryWasDown=leftPrimary; leftSecondaryWasDown=leftSecondary;
                return;
            }
            if(!ExperimentStarted)
            {
                UpdateStartMenu(axis);
                if(leftStickPressed) EnterValidation();
                if(secondary && !secondaryWasDown) SetTunnelFov(selectedTunnelFov==15 ? 30 : 15);
                // X is shown in the menu and used by the Quest controller. S remains an alias
                // for older desktop instructions.
                bool simulationKey=Keyboard.current!=null &&
                    (Keyboard.current.xKey.wasPressedThisFrame || Keyboard.current.sKey.wasPressedThisFrame);
                if((leftPrimary && !leftPrimaryWasDown) || simulationKey) SetSimulationEnabled(!simulationEnabled);
                bool gazeSourceKey=Keyboard.current!=null && Keyboard.current.tKey.wasPressedThisFrame;
                if((leftSecondary && !leftSecondaryWasDown) || gazeSourceKey)
                    SetGazeSource(selectedGazeSource==GazeSource.HeadsetOnly ? GazeSource.PupilLabs : GazeSource.HeadsetOnly);
                bool desktopClick=Mouse.current!=null && Mouse.current.leftButton.wasPressedThisFrame;
                if((primary && !primaryWasDown) || startKey || desktopClick) BeginExperiment();
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

        IEnumerator EnsureCameraAboveFloor()
        {
            // Let OpenXR/Meta tracking publish its Floor-Level head pose first. If it still
            // reports the camera at the floor, lift the rig origin once so the headset view
            // starts at eye height instead of rendering from ground level.
            for(int i=0;i<20;i++)
            {
                yield return new WaitForSecondsRealtime(.1f);
                if(!participantCamera || participantCamera.transform.position.y>.8f) yield break;
            }
            if(!participantCamera) yield break;
            var rig=participantCamera.GetComponentInParent<OVRCameraRig>();
            if(!rig) yield break;
            float currentHeight=participantCamera.transform.position.y;
            if(currentHeight<.8f)
            {
                float lift=seatedEyeHeight-currentHeight;
                rig.transform.position+=Vector3.up*lift;
                Debug.LogWarning($"[SeatedPlatform] Head pose remained at y={currentHeight:0.00} m; lifted camera rig by {lift:0.00} m to eye height.",this);
            }
        }
        public static string FormatParticipantId(int number) => "P"+Mathf.Clamp(number,1,9999).ToString("D3");
        public void SetParticipantNumber(int number)
        {
            participantNumber=Mathf.Clamp(number,1,9999);
            if(menuParticipantText) menuParticipantText.text=FormatParticipantId(participantNumber);
        }
        public void SetTunnelFov(int degrees)
        {
            selectedTunnelFov=degrees==30 ? 30 : 15;
            if(menuTunnelText)
                menuTunnelText.text=$"{selectedTunnelFov}°";
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
                menuGazeSourceText.text=usePupilLabs ? "Pupil Labs" : "Headset only";
            if(menuStatusText)
            {
                menuStatusText.color=usePupilLabs ? UiWarn : UiGood;
                menuStatusText.text=usePupilLabs ? "Waiting for eye tracking..." : "Ready";
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
                if(hasValidatedFloor)
                {
                    menuStatusText.color=UiGood;
                    menuStatusText.text="Ready  ·  room setup confirmed";
                    return;
                }
                if(selectedGazeSource==GazeSource.HeadsetOnly)
                {
                    menuStatusText.color=UiGood;
                    menuStatusText.text="Ready";
                    return;
                }
                bool ready=gaze && gaze.Current.valid;
                string reason=gaze && !string.IsNullOrEmpty(gaze.Current.invalidReason) ? "\n"+gaze.Current.invalidReason : "";
                menuStatusText.color=ready ? UiGood : UiWarn;
                menuStatusText.text=ready ? "Ready  ·  eye tracking OK" : "Waiting for eye tracking..."+reason;
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
            {
                menuSimulationText.text=simulationEnabled ? "ON" : "OFF";
                menuSimulationText.color=simulationEnabled ? UiGood : UiMuted;
            }
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
            menuAvatarText.text="";
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
            if(corridorPreviewAvatar) corridorPreviewAvatar.SetActive(false);
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
                gaze && gaze.rpField ? gaze.rpField.overlay : null,logger,participantNumber,seatedEyeHeight,hasValidatedFloor,validatedFloorY,corridorTJunction);
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
            if(corridorPreviewAvatar) corridorPreviewAvatar.SetActive(false);
            if(headsetStatus) headsetStatus.gameObject.SetActive(false);
            if(startMenuCanvas) startMenuCanvas.gameObject.SetActive(true);
            SetGazeSource(selectedGazeSource);
        }
        static readonly Color UiPanel=new Color(.03f,.05f,.08f,.97f), UiRow=new Color(.07f,.11f,.16f,1f), UiFrame=new Color(.30f,.42f,.52f,1f),
            UiAccent=new Color(.35f,.85f,1f,1f), UiGood=new Color(.40f,1f,.60f,1f), UiWarn=new Color(1f,.75f,.30f,1f), UiMuted=new Color(.62f,.70f,.78f,1f);

        /// <summary>A framed box with a controller button symbol or letter in it.</summary>
        void AddKeyBadge(Transform parent,Vector2 position,float width,string key,Color frame)
        {
            var box=NewUi<Image>(parent,"Key "+key,position,new Vector2(width,58));
            box.color=new Color(.02f,.03f,.05f,1f); box.material=menuMaterial;
            var outline=box.gameObject.AddComponent<Outline>(); outline.effectColor=frame; outline.effectDistance=new Vector2(3,-3); outline.useGraphicAlpha=false;
            var label=NewText(box.transform,"Key Label",Vector2.zero,new Vector2(width,58),key,key.Length<=2 ? 34 : 20,FontStyle.Bold);
            label.margin=Vector4.zero; label.color=frame; label.alignment=TMPro.TextAlignmentOptions.Center;
        }

        /// <summary>One framed menu row: [key] label ............ value. Returns the value text.</summary>
        TMPro.TextMeshProUGUI AddMenuRow(Transform parent,float y,string key,string label,Color frame,float keyWidth=96)
        {
            var row=NewUi<Image>(parent,"Row "+label,new Vector2(0,y),new Vector2(820,80));
            row.color=UiRow; row.material=menuMaterial;
            var outline=row.gameObject.AddComponent<Outline>(); outline.effectColor=UiFrame; outline.effectDistance=new Vector2(2,-2); outline.useGraphicAlpha=false;
            AddKeyBadge(row.transform,new Vector2(-410+20+keyWidth*.5f,0),keyWidth,key,frame);
            var name=NewText(row.transform,"Label",new Vector2(40+keyWidth*.5f-48,0),new Vector2(460,70),label,30,FontStyle.Normal);
            name.alignment=TMPro.TextAlignmentOptions.Left; name.margin=Vector4.zero; name.color=new Color(.90f,.95f,1f,1f);
            var value=NewText(row.transform,"Value",new Vector2(250,0),new Vector2(280,70),"",32,FontStyle.Bold);
            value.alignment=TMPro.TextAlignmentOptions.Right; value.margin=new Vector4(0,0,24,0); value.color=Color.white;
            return value;
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
            var rect=root.GetComponent<RectTransform>(); rect.sizeDelta=new Vector2(900,980);
            menuMaterial=new Material(Shader.Find("UI/Default")); menuMaterial.renderQueue=5000;
            // Menu draws in queue 5000 (after the RP overlay at 4900). Never use >5000: URP only renders transparent queues 2501-5000.
            var panel=NewUi<Image>(root.transform,"Panel",Vector2.zero,new Vector2(900,980));
            panel.color=UiPanel; panel.material=menuMaterial;
            var panelFrame=panel.gameObject.AddComponent<Outline>(); panelFrame.effectColor=UiAccent; panelFrame.effectDistance=new Vector2(4,-4); panelFrame.useGraphicAlpha=false;

            var title=NewText(root.transform,"Title",new Vector2(0,415),new Vector2(820,70),"WALKING STUDY",48,FontStyle.Bold);
            title.color=Color.white;
            menuRunModeHelpText=NewText(root.transform,"Subtitle",new Vector2(0,360),new Vector2(820,40),"",24,FontStyle.Normal);
            menuRunModeHelpText.color=UiMuted;

            menuParticipantText=AddMenuRow(root.transform,265,"STICK","Participant",UiAccent,120);
            menuSimulationText =AddMenuRow(root.transform,170,"X","RP simulation",UiAccent,120);
            menuTunnelText     =AddMenuRow(root.transform,75,"B","Field size",UiAccent,120);
            menuGazeSourceText =AddMenuRow(root.transform,-20,"Y","Eye tracking",UiAccent,120);
            var roomValue      =AddMenuRow(root.transform,-115,"L-CLICK","Room setup check",UiAccent,120);
            roomValue.text="optional"; roomValue.color=UiMuted; roomValue.fontSize=26; menuRoomValueText=roomValue;

            menuStatusText=NewText(root.transform,"Status",new Vector2(0,-200),new Vector2(820,60),"",26,FontStyle.Bold);
            menuStatusText.color=UiGood;

            // Start: the one big action.
            var start=NewUi<Image>(root.transform,"Row Start",new Vector2(0,-300),new Vector2(820,100));
            start.color=new Color(.06f,.20f,.12f,1f); start.material=menuMaterial;
            var startFrame=start.gameObject.AddComponent<Outline>(); startFrame.effectColor=UiGood; startFrame.effectDistance=new Vector2(3,-3); startFrame.useGraphicAlpha=false;
            AddKeyBadge(start.transform,new Vector2(-410+20+60,0),120,"A",UiGood);
            var startLabel=NewText(start.transform,"Start Label",new Vector2(60,0),new Vector2(560,90),"START",44,FontStyle.Bold);
            startLabel.color=UiGood; startLabel.margin=Vector4.zero;

            var footer=NewText(root.transform,"Footer",new Vector2(0,-415),new Vector2(820,60),
                "During the session:  hold LEFT Y for 1.5 s to stop\nKeyboard:  arrows · X · B · T · Space",19,FontStyle.Normal);
            footer.color=UiMuted;

            SetParticipantNumber(participantNumber);
            SetTunnelFov(selectedTunnelFov);
            SetGazeSource(selectedGazeSource);
            UpdateAvatarLabel();
            SetSimulationEnabled(simulationEnabled);
            UpdateRunModeLabel();
        }
        TMPro.TextMeshProUGUI menuRoomValueText;

        void EnterValidation()
        {
            validationActive=true;
            startMenuCanvas.gameObject.SetActive(false);
            validationFloorY=ResolveConfirmedFloorY(out validationFloorSource);
            BuildValidationFloorMarker();
            BuildValidationMenu(validationFloorSource);
        }

        // Quest Guardian's setup establishes the Floor Level tracking origin. That is the
        // physical floor the app sees at world Y=0; no separate room loader is required.
        float ResolveConfirmedFloorY(out string source)
        {
            if(!XRSettings.isDeviceActive)
            {
                var origin=participantCamera.transform.position;
                if(Physics.Raycast(origin+Vector3.up*.25f,Vector3.down,out var hit,6f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore))
                { source="Editor floor collider: "+hit.collider.name; return hit.point.y; }
                source="Editor fallback floor (y = 0)"; return 0f;
            }
            var rig=participantCamera.GetComponentInParent<OVRCameraRig>();
            var manager=rig ? rig.GetComponent<OVRManager>() : null;
            if(manager) manager.trackingOriginType=OVRManager.TrackingOrigin.FloorLevel;
            source="Quest Guardian floor · Floor Level tracking origin";
            return 0f;
        }

        void BuildValidationMenu(string floorSource)
        {
            var root=new GameObject("Room Setup Validation Menu",typeof(RectTransform),typeof(Canvas));
            root.transform.SetParent(participantCamera.transform,false);
            root.transform.localPosition=new Vector3(0,0,1.15f); root.transform.localScale=Vector3.one*.001f;
            validationCanvas=root.GetComponent<Canvas>(); validationCanvas.renderMode=RenderMode.WorldSpace;
            validationCanvas.worldCamera=participantCamera; validationCanvas.overrideSorting=true; validationCanvas.sortingOrder=560;
            var rect=root.GetComponent<RectTransform>(); rect.sizeDelta=new Vector2(900,820);
            var panel=NewUi<Image>(root.transform,"Panel",Vector2.zero,new Vector2(900,820));
            panel.color=UiPanel; if(menuMaterial) panel.material=menuMaterial;
            var frame=panel.gameObject.AddComponent<Outline>(); frame.effectColor=UiAccent; frame.effectDistance=new Vector2(4,-4); frame.useGraphicAlpha=false;
            var title=NewText(root.transform,"Title",new Vector2(0,345),new Vector2(820,60),"ROOM SETUP CHECK",42,FontStyle.Bold);
            title.color=Color.white;
            validationStatus=NewText(root.transform,"Floor status",new Vector2(0,265),new Vector2(820,90),$"FLOOR: {floorSource}\nHeight: {validationFloorY:0.000} m",22,FontStyle.Normal);
            validationStatus.color=UiMuted;
            AddMenuRow(root.transform,160,"X","Place the walker",UiAccent,120);
            AddMenuRow(root.transform,70,"A","Test walk",UiAccent,120);
            AddMenuRow(root.transform,-20,"B","Reset",UiAccent,120);
            var confirm=AddMenuRow(root.transform,-110,"Y","Floor is correct",UiGood,120);
            confirm.text="confirm"; confirm.color=UiGood; confirm.fontSize=26;
            var back=AddMenuRow(root.transform,-200,"L-CLICK","Back to menu",UiMuted,120);
            back.text="cancel"; back.color=UiMuted; back.fontSize=26;
            var hint=NewText(root.transform,"Hint",new Vector2(0,-310),new Vector2(820,70),"Check that the green grid lies on the real floor.\nKeyboard:  X · A / Space · R · Y",20,FontStyle.Normal);
            hint.color=UiMuted;
        }

        void BuildValidationFloorMarker()
        {
            float radius=3f, y=validationFloorY+.012f;
            var go=new GameObject("Detected Floor Validation Grid");
            var mat=new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
            mat.color=new Color(.12f,1f,.25f,.95f);
            int divisions=12;
            for(int i=0;i<=divisions;i++)
            {
                float offset=-radius+2*radius*i/divisions;
                MakeValidationLine(go.transform,mat,new Vector3(-radius,y,offset),new Vector3(radius,y,offset));
                MakeValidationLine(go.transform,mat,new Vector3(offset,y,-radius),new Vector3(offset,y,radius));
            }
            validationGridRoot=go;
            var center=participantCamera.transform.position; center.y=validationFloorY;
            validationGridRoot.transform.position=new Vector3(Mathf.Round(center.x),0,Mathf.Round(center.z));
        }
        GameObject validationGridRoot;
        void MakeValidationLine(Transform parent,Material mat,Vector3 a,Vector3 b)
        {
            var go=new GameObject("Floor grid line"); go.transform.SetParent(parent,false);
            var line=go.AddComponent<LineRenderer>(); line.useWorldSpace=false; line.positionCount=2; line.SetPosition(0,a); line.SetPosition(1,b);
            line.startWidth=line.endWidth=.012f; line.sharedMaterial=mat; line.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void UpdateValidation(bool addAvatar,bool testWalk,bool reset,bool confirm,bool keyAdd,bool keyWalk,bool keyReset,bool keyConfirm,bool cancel)
        {
            UpdateValidationWalk();
            if(cancel) { ExitValidation(false); return; }
            if(addAvatar||keyAdd) PlaceValidationAvatar();
            if(testWalk||keyWalk) ToggleValidationWalk();
            if(reset||keyReset) ResetValidationAvatar();
            if(confirm||keyConfirm) ExitValidation(true);
        }

        void PlaceValidationAvatar()
        {
            ResetValidationAvatar();
            var library=MocapClipLibrary.Load();
            var clips=library.For(PerceptionPilot.ManeuverType.Straight);
            if(clips.Count==0) { SetValidationStatus("No straight recorded walk is available in the app."); return; }
            validationClip=clips[0];
            for(int i=1;i<clips.Count;i++)
                if(Mathf.Abs(clips[i].info.measuredTurnDeg)<Mathf.Abs(validationClip.info.measuredTurnDeg)) validationClip=clips[i];
            validationStartTimeRelT0=Mathf.Max(-2f,-validationClip.T0);
            validationFigure=MocapFigure.Create(MocapFigure.Mode.Realistic);
            validationFigure.AttachAvatar(MocapAvatarDriver.Create(avatarPrefab,avatarRigAsset));
            if(!validationFigure.HasAvatar || !validationFigure.Avatar.Prepare(validationClip))
            { SetValidationStatus("Avatar could not load the recorded walk. Check the humanoid avatar setup."); ResetValidationAvatar(); return; }
            validationAvatarRoot=validationFigure.gameObject; validationAvatarRoot.name="Room Validation Recorded Avatar";
            validationPose=new Vector3[MocapClipLibrary.JointNames.Length]; validationWorldPose=new Vector3[validationPose.Length];
            Vector3 flatForward=Vector3.ProjectOnPlane(participantCamera.transform.forward,Vector3.up).normalized;
            if(flatForward.sqrMagnitude<.01f) flatForward=Vector3.forward;
            validationRotation=Quaternion.LookRotation(flatForward,Vector3.up);
            validationClip.Sample(validationClip.T0,validationPose);
            float lowest=Mathf.Min(validationPose[MocapClipLibrary.LToe].y,validationPose[MocapClipLibrary.RToe].y);
            Vector3 target=participantCamera.transform.position+flatForward*3f; target.y=validationFloorY;
            validationOrigin=target-validationRotation*new Vector3(validationPose[MocapClipLibrary.Hips].x,0,validationPose[MocapClipLibrary.Hips].z)+Vector3.up*(validationFloorY-lowest);
            SetValidationStatus($"Avatar placed on the detected floor. Clip {validationClip.info.id}; press A to play the full walk.");
            ApplyValidationPose(validationClip.T0+validationStartTimeRelT0);
        }
        Quaternion validationRotation; Vector3 validationOrigin;
        void ApplyValidationPose(float clipTime)
        {
            if(!validationFigure||validationClip==null) return;
            validationClip.Sample(clipTime,validationPose);
            for(int i=0;i<validationPose.Length;i++) validationWorldPose[i]=validationOrigin+validationRotation*validationPose[i];
            validationFigure.SetPose(validationWorldPose);
            validationFigure.Avatar.SetPose(validationClip,clipTime,validationOrigin,validationRotation,validationWorldPose);
        }
        void ToggleValidationWalk()
        {
            if(!validationFigure) { PlaceValidationAvatar(); if(!validationFigure) return; }
            validationWalking=true;
            validationMotionStarted=Time.time;
            ApplyValidationPose(validationClip.T0+validationStartTimeRelT0);
            SetValidationStatus("Test walking: CMU recorded straight walk. Press A to replay, X to replace, B to reset.");
        }
        void UpdateValidationWalk()
        {
            if(!validationWalking||!validationFigure||validationClip==null) return;
            float t=validationClip.T0+validationStartTimeRelT0+Time.time-validationMotionStarted;
            float end=validationClip.Duration;
            if(t>=end) { validationWalking=false; ApplyValidationPose(end); SetValidationStatus("Walk complete. Press A to replay, X to replace, B to reset."); return; }
            ApplyValidationPose(t);
        }
        void ResetValidationAvatar()
        {
            validationWalking=false;
            if(validationAvatarRoot) Destroy(validationAvatarRoot);
            validationAvatarRoot=null; validationFigure=null; validationClip=null;
            SetValidationStatus("Floor grid is visible. Press X to place the avatar and A to test walking.");
        }
        void SetValidationStatus(string text) { if(validationStatus) validationStatus.text=$"FLOOR: {validationFloorSource} · y = {validationFloorY:0.000} m\n{text}"; }
        void ExitValidation(bool confirmed)
        {
            if(!validationActive) return;
            validationActive=false; ResetValidationAvatar();
            if(validationCanvas) Destroy(validationCanvas.gameObject); validationCanvas=null; validationStatus=null;
            if(validationGridRoot) Destroy(validationGridRoot); validationGridRoot=null;
            validatedFloorY=validationFloorY; hasValidatedFloor=confirmed;
            if(startMenuCanvas) startMenuCanvas.gameObject.SetActive(true);
            if(confirmed&&menuStatusText) menuStatusText.text=$"SETUP CONFIRMED · floor y = {validatedFloorY:0.000} m";
            if(menuRoomValueText) { menuRoomValueText.text=confirmed ? "done" : "optional"; menuRoomValueText.color=confirmed ? UiGood : UiMuted; }
        }
        float validatedFloorY;
        bool hasValidatedFloor;
        void AddMenuCard(Transform parent,string objectName,float y,float height,Color color)
        {
            var card=NewUi<Image>(parent,objectName,new Vector2(0,y),new Vector2(880,height));
            card.color=color; card.material=menuMaterial;
        }
        void UpdateRunModeLabel()
        {
            if(menuRunModeText) menuRunModeText.text="FULL EXPERIMENT";
            // Counts come from the recorded clips that exist; maneuvers without a verified clip are left out.
            var design=PerceptionPilotDemoRunner.DescribeDesign().Split('|');
            if(menuRunModeHelpText) menuRunModeHelpText.text="Full experiment  ·  "+design[0]+" trials";
            if(menuTrialSummaryText) menuTrialSummaryText.text="";
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

        void DisablePassthroughForVirtualScene()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig)
            {
                var manager = rig.GetComponent<OVRManager>();
                if (manager) manager.isInsightPassthroughEnabled = false;
                foreach (var layer in rig.GetComponentsInChildren<OVRPassthroughLayer>(true))
                    layer.enabled = false;
            }
            if (participantCamera)
            {
                participantCamera.clearFlags = CameraClearFlags.SolidColor;
                participantCamera.backgroundColor = new Color(0.025f, 0.025f, 0.035f, 1f);
            }
        }

        void CreateCorridorPreview()
        {
            if (corridorEnvironmentRoot) return;
            var source = Resources.Load<GameObject>("ExperimentEnvironment/office");
            if (!source)
            {
                Debug.LogError("[SeatedPlatform] Corridor FBX was not found at Assets/Resources/ExperimentEnvironment/office.fbx.", this);
                return;
            }

            corridorEnvironmentRoot = new GameObject("CGTrader Corridor Environment");
            var corridor = Instantiate(source, corridorEnvironmentRoot.transform, false);
            corridor.name = "Corridor FBX";
            Bounds bounds = GetRenderBounds(corridor);
            // The downloaded hallway is authored along X; face it down Unity's +Z axis.
            if (bounds.size.x > bounds.size.z * 1.2f)
                corridorEnvironmentRoot.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            bounds = GetRenderBounds(corridor);
            corridorEnvironmentRoot.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, 1f - bounds.min.z);
            bounds = GetRenderBounds(corridor);

            foreach (var renderer in corridor.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            foreach (var filter in corridor.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.sharedMesh || filter.GetComponent<Collider>()) continue;
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
            }

            corridorPreviewAvatar = Instantiate(avatarPrefab,
                new Vector3(0f, 0f, (bounds.min.z + bounds.max.z) * 0.5f),
                Quaternion.LookRotation(Vector3.forward, Vector3.up));
            corridorPreviewAvatar.name = "Corridor Center Avatar Preview";
            var animator = corridorPreviewAvatar.GetComponentInChildren<Animator>(true);
            if (!animator) animator = corridorPreviewAvatar.AddComponent<Animator>();
            if (avatarRigAsset) animator.avatar = avatarRigAsset;
            if (animationController) animator.runtimeAnimatorController = animationController;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var collider in corridorPreviewAvatar.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            foreach (var renderer in corridorPreviewAvatar.GetComponentsInChildren<Renderer>(true))
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            var avatarBounds = GetRenderBounds(corridorPreviewAvatar);
            corridorPreviewAvatar.transform.position += Vector3.up * -avatarBounds.min.y;
            Debug.Log($"[SeatedPlatform] Virtual corridor ready ({bounds.size.x:F1} x {bounds.size.y:F1} x {bounds.size.z:F1} m); avatar preview placed at corridor center, z={(bounds.min.z + bounds.max.z) * 0.5f:F1} m.", this);
        }

        static Bounds GetRenderBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.one);
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
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
