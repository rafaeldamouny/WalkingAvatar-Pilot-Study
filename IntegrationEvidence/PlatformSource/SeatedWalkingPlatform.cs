using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace WalkingEyeExperiment
{
    public class SeatedWalkingPlatform : MonoBehaviour
    {
        public GameObject avatarPrefab;
        public Avatar avatarRigAsset;
        public RuntimeAnimatorController animationController;
        public Camera participantCamera;
        public NeonGazeBridge gaze;
        public ResearchSessionLogger logger;
        public Text headsetStatus;
        public float initialDistance = 3;
        public float pathLength = 6;
        public float seatedEyeHeight = 1.2f;
        public float walkingSpeed = 1;
        public bool autoWalk = true;
        public bool showStatus = true;
        public Transform AvatarInstance { get; private set; }
        SimpleWalkDriver driver;
        bool started, primaryWasDown, secondaryWasDown, stickWasDown;
        float readyAt;
        int repeatIndex;
        float nextStatus;

        void Start()
        {
            readyAt=Time.unscaledTime+.75f;
            if(!avatarPrefab || !participantCamera) { Debug.LogError("[SeatedPlatform] Existing avatar and participant camera required",this); enabled=false; }
        }
        void Update()
        {
            if(!started && Time.unscaledTime>=readyAt) { CreateAvatar(); started=true; }
            var device=InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            device.TryGetFeatureValue(CommonUsages.primaryButton,out bool primary);
            device.TryGetFeatureValue(CommonUsages.secondaryButton,out bool secondary);
            device.TryGetFeatureValue(CommonUsages.primary2DAxisClick,out bool stick);
            bool walkKey=Keyboard.current!=null && Keyboard.current.spaceKey.wasPressedThisFrame;
            bool centerKey=Keyboard.current!=null && Keyboard.current.gKey.wasPressedThisFrame;
            bool debugKey=Keyboard.current!=null && Keyboard.current.vKey.wasPressedThisFrame;
            if((primary && !primaryWasDown) || walkKey) ToggleWalk();
            if((secondary && !secondaryWasDown) || centerKey) ToggleCenter();
            if((stick && !stickWasDown) || debugKey) gaze.debugVisualization=!gaze.debugVisualization;
            if(Keyboard.current!=null && Keyboard.current.rKey.wasPressedThisFrame) RestartWalk();
            primaryWasDown=primary; secondaryWasDown=secondary; stickWasDown=stick;
            if(headsetStatus && Time.unscaledTime>=nextStatus)
            {
                headsetStatus.gameObject.SetActive(showStatus);
                var frame=gaze.Current;
                string tracking=gaze.useSyntheticGaze ? "SYNTHETIC TEST" : frame.valid ? "NEON: gaze received" : "NEON: "+frame.invalidReason;
                string center=gaze.rpField.centerMode==RpFieldDriver.CenterMode.GazeCentered && frame.valid ? "Gaze centered" : "Head centered";
                headsetStatus.text=tracking+"\n"+center+" | hit: "+(frame.bodyRegion ?? "None")+"\n"+
                    (logger.Recording ? "CSV recording ("+logger.RowsWritten+" rows)" : "CSV stopped: "+logger.Error)+"\n"+
                    "A: walk/pause | B: RP center | stick: gaze debug";
                nextStatus=Time.unscaledTime+.2f;
            }
        }
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
            var animator=instance.GetComponentInChildren<Animator>();
            if(!animator) { Debug.LogError("[SeatedPlatform] Existing prefab has no Animator",this); return; }
            if(avatarRigAsset) animator.avatar=avatarRigAsset;
            if(animationController) animator.runtimeAnimatorController=animationController;
            animator.applyRootMotion=false;
            animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            var regions=instance.AddComponent<AvatarRegionRig>();
            regions.animator=animator;
            gaze.avatarRegions=regions;
            logger.avatar=instance.transform;
            driver=instance.AddComponent<SimpleWalkDriver>();
            driver.MoveSpeed=walkingSpeed;
            driver.LineWidth=0;
            driver.SetPath(new List<Vector3>{position,position+forward*pathLength});
            driver.OnGoalReached+=OnGoal;
            if(autoWalk) StartNewWalk(); else driver.StopWalk();
        }
        void OnGoal() { logger.EndTrial(); }
        void StartNewWalk()
        {
            if(!driver) return;
            logger.BeginTrial("platform_"+(++repeatIndex),logger.condition,logger.avatarExemplar);
            driver.StartWalk();
        }
        public void ToggleWalk()
        {
            if(!driver) return;
            if(driver.IsWalking) driver.StopWalk();
            else if(driver.Progress>=.99f) RestartWalk();
            else driver.StartWalk();
        }
        public void RestartWalk() { if(driver) { driver.ResetToStart(); StartNewWalk(); } }
        public void ToggleCenter()
        {
            gaze.rpField.centerMode=gaze.rpField.centerMode==RpFieldDriver.CenterMode.HeadCentered ? RpFieldDriver.CenterMode.GazeCentered : RpFieldDriver.CenterMode.HeadCentered;
        }
    }
}
