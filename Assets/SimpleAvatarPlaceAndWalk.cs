using UnityEngine;
using UnityEngine.XR;

public class SimpleAvatarPlaceAndWalk : MonoBehaviour
{
    [Header("Avatar")]
    public Transform avatarRoot;
    public Animator avatarAnimator;
    public string walkBool = "isWalking";
    public float walkSpeed = 1.0f;

    [Header("Follow While Unplaced")]
    public Transform followPoint;         // assign controller anchor transform
    public Vector3 positionOffset = new Vector3(0, 0, 0.5f); // in front of controller
    public Vector3 eulerOffset = Vector3.zero;

    [Header("Input")]
    public XRNode controllerHand = XRNode.RightHand;

    private bool isPlaced = false;
    private bool isWalking = false;
    private bool wasPressed = false;

    void Start()
    {
        if (avatarAnimator != null)
            avatarAnimator.SetBool(walkBool, false);

        // IMPORTANT: prevent animation moving the avatar
        if (avatarAnimator != null)
            avatarAnimator.applyRootMotion = false;
    }

    void Update()
    {
        // 1) If not placed -> follow controller transform
        if (!isPlaced && avatarRoot != null && followPoint != null)
        {
            avatarRoot.position = followPoint.TransformPoint(positionOffset);
            avatarRoot.rotation = followPoint.rotation * Quaternion.Euler(eulerOffset);
        }

        // 2) Read A button (OpenXR primary button)
        bool pressed = false;
        var device = InputDevices.GetDeviceAtXRNode(controllerHand);
        if (device.isValid)
            device.TryGetFeatureValue(CommonUsages.primaryButton, out pressed);

        bool down = pressed && !wasPressed;
        wasPressed = pressed;

        if (down)
        {
            if (!isPlaced)
            {
                // First press: place
                isPlaced = true;
                isWalking = false;
                if (avatarAnimator != null) avatarAnimator.SetBool(walkBool, false);
                return;
            }

            // After placed: toggle walk/stop
            isWalking = !isWalking;
            if (avatarAnimator != null) avatarAnimator.SetBool(walkBool, isWalking);
        }

        // 3) Move forward when walking (only after placed)
        if (isPlaced && isWalking && avatarRoot != null)
        {
            avatarRoot.Translate(Vector3.forward * walkSpeed * Time.deltaTime, Space.Self);
        }
    }
}
