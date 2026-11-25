using UnityEngine;

namespace ReadyPlayerMe.Samples.QuickStart
{
    [RequireComponent(typeof(ThirdPersonMovement))]
    public class AutoWalkController : MonoBehaviour
    {
        [Header("Auto Walk Settings")]
        [SerializeField] private float walkDistance = 10f; // Distance to walk
        [SerializeField] private float walkSpeed = 1.2f; // Speed (slow/medium/fast)
        [SerializeField] private float delayBeforeStart = 2f; // Countdown before starting

        [Header("References")]
        [SerializeField] private GameObject avatar;
        [SerializeField] private Animator animator;

        private ThirdPersonMovement movement;
        private Vector3 startPosition;
        private float distanceWalked = 0f;
        private bool isWalking = false;
        private bool hasStarted = false;

        // Animator parameter hashes
        private static readonly int MoveSpeedHash = Animator.StringToHash("MoveSpeed");
        private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");

        void Start()
        {
            movement = GetComponent<ThirdPersonMovement>();

            // Get avatar and animator if not assigned
            if (avatar == null)
            {
                // Assuming the avatar instance is the first child of the RPM Player root.
                if (transform.childCount > 0)
                {
                    avatar = transform.GetChild(0).gameObject;
                }
            }

            if (animator == null && avatar != null)
            {
                animator = avatar.GetComponent<Animator>();
            }

            // ***********************************************
            // FIX for NullReferenceException: Call Setup()
            // ***********************************************
            if (movement != null && avatar != null)
            {
                // Pass the avatar object to the ThirdPersonMovement script.
                movement.Setup(avatar);
            }
            // ***********************************************

            startPosition = transform.position;

            // Start after delay
            Invoke(nameof(StartWalking), delayBeforeStart);
        }
        void StartWalking()
        {
            isWalking = true;
            hasStarted = true;
            Debug.Log("Avatar started walking");
        }

        void Update()
        {
            if (!hasStarted || !isWalking) return;

            // Calculate distance walked
            distanceWalked = Vector3.Distance(startPosition, transform.position);

            // Check if reached target distance
            if (distanceWalked >= walkDistance)
            {
                StopWalking();
                return;
            }

            // ***********************************************
            // NEW FIX: Force the character into the 'walk' state
            // ***********************************************
            if (movement != null)
            {
                movement.SetIsRunning(false); // <--- Add this line!
            }
            // ***********************************************

            // Move forward (Y axis = forward)
            movement.Move(0f, 1f); // inputX = 0, inputY = 1 (forward)

            // Update animator
            if (animator != null)
            {
                // The MoveSpeed will now be 'walkSpeed' (3f) in ThirdPersonMovement,
                // and the animator will receive your desired 'walkSpeed' (1.2f).
                animator.SetFloat(MoveSpeedHash, walkSpeed);
                animator.SetBool(IsGroundedHash, true);
            }
        }
        void StopWalking()
        {
            isWalking = false;

            // Stop animation
            if (animator != null)
            {
                animator.SetFloat(MoveSpeedHash, 0f);
            }

            Debug.Log($"Avatar stopped after walking {distanceWalked:F2} meters");
        }

        public bool IsWalking => isWalking;
        public float DistanceWalked => distanceWalked;
    }
}