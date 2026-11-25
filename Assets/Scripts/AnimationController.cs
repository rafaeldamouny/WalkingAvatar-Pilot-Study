using UnityEngine;
using UnityEngine.InputSystem;
using XRNode        = UnityEngine.XR.XRNode;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;
using CommonUsages  = UnityEngine.XR.CommonUsages;

public class AnimationController : MonoBehaviour
{
    [Header("Avatar (auto-found from AvatarPlacer if empty)")]
    public Animator  avatarAnimator;
    public Transform avatarRoot;

    [Header("Movement")]
    public float walkSpeed = 1.0f;

    [Header("Controller Input")]
    public XRNode controllerHand = XRNode.RightHand;

    // StarterAssetsThirdPerson.controller parameters
    private static readonly int SpeedHash       = Animator.StringToHash("Speed");
    private static readonly int GroundedHash    = Animator.StringToHash("Grounded");
    private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");

    private bool isWalking        = false;
    private bool wasButtonPressed = false;
    private XRInputDevice controller;

    void Start()
    {
        if (avatarAnimator == null || avatarRoot == null)
        {
            var placer = FindObjectOfType<AvatarPlacer>();
            if (placer != null)
            {
                if (avatarAnimator == null) avatarAnimator = placer.GetAvatarAnimator();
                if (avatarRoot     == null) avatarRoot     = placer.GetAvatarRoot();
            }
        }

        if (avatarAnimator != null && avatarAnimator.runtimeAnimatorController != null)
        {
            avatarAnimator.SetFloat(SpeedHash, 0f);
            avatarAnimator.SetBool(GroundedHash, true);
            avatarAnimator.SetFloat(MotionSpeedHash, 1f);
        }
    }

    void Update()
    {
#if UNITY_EDITOR
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            ToggleWalk();

        if (isWalking && avatarRoot != null)
            avatarRoot.Translate(Vector3.forward * walkSpeed * Time.deltaTime, Space.Self);
        return;
#endif
        if (!controller.isValid)
        {
            controller = XRInputDevices.GetDeviceAtXRNode(controllerHand);
            return;
        }

        bool buttonPressed = false;
        controller.TryGetFeatureValue(CommonUsages.primaryButton, out buttonPressed);
        if (buttonPressed && !wasButtonPressed) ToggleWalk();
        wasButtonPressed = buttonPressed;

        if (isWalking && avatarRoot != null)
            avatarRoot.Translate(Vector3.forward * walkSpeed * Time.deltaTime, Space.Self);
    }

    void ToggleWalk()
    {
        isWalking = !isWalking;
        if (avatarAnimator != null && avatarAnimator.runtimeAnimatorController != null)
        {
            avatarAnimator.SetFloat(SpeedHash, isWalking ? 0.5f : 0f);
            avatarAnimator.SetFloat(MotionSpeedHash, 1f);
            avatarAnimator.SetBool(GroundedHash, true);
        }
        Debug.Log($"[AnimationController] Walking: {isWalking}");
    }

    public bool IsWalking => isWalking;
}
