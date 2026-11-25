using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 3-phase placement flow:
///   Phase 1 — place avatar / start point
///   Phase 2 — place goal marker (yellow sphere → cyan permanent)
///   Phase 3 — press B to start walking
///
/// XR: all phases use camera-relative right thumbstick movement.
///     rightController raycast used for Phase 1 IF the field is assigned — otherwise thumbstick.
///     Left thumbstick = rotate avatar (Phase 1 only).
/// </summary>
public class AvatarPlacer : MonoBehaviour
{
    [Header("References")]
    public GameObject avatarPrefab;
    public Transform  rightController;           // optional — for floor-raycast placement
    public RuntimeAnimatorController animatorController;

    [Header("Placement Settings")]
    public float previewAlpha   = 0.4f;
    public float floorOffset    = 0.01f;
    public float maxRayDistance = 10f;
    public float rotationSpeed  = 90f;
    public float placeMoveSpeed = 2.0f;

    [Header("Avatar Size")]
    [Tooltip("1.0 = original prefab size. 1.1 gives a taller adult look.")]
    public float avatarScale = 1.1f;

    [Header("Auto-Walk")]
    public float walkSpeed = 1.2f;
    [Tooltip("Drag the Avatar sub-asset from casual-f-0170.fbx here. " +
             "Without this the skeleton stays in T-pose while the body moves.")]
    public Avatar avatarAsset;

    [Header("Materials")]
    public Material transparentMaterial;

    // ── Animator hashes ───────────────────────────────────────────
    private static readonly int SpeedHash       = Animator.StringToHash("Speed");
    private static readonly int GroundedHash    = Animator.StringToHash("Grounded");
    private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");

    // ── State ─────────────────────────────────────────────────────
    private GameObject     previewAvatar;
    private Animator       avatarAnimator;
    private AutoWalkDriver walkDriver;
    private ObstaclePlacer obstaclePlacer;

    private bool didSpawn  = false;
    private bool isPlaced  = false;
    private bool goalSet   = false;
    private bool isWalking = false;

    private Renderer[]   renderers;
    private Material[][] originalMaterials;
    private float        rotationAngle = 0f;

    // ── Goal ─────────────────────────────────────────────────────
    private GameObject _goalPreview;
    private GameObject _goalMarker;
    private Vector3    _goalPos;

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
        obstaclePlacer = FindObjectOfType<ObstaclePlacer>();
        if (obstaclePlacer == null)
        {
            SpawnPreview();
            didSpawn = true;
        }
    }

    // ─────────────────────────────────────────────────────────────
    void SpawnPreview()
    {
        if (avatarPrefab == null)
        {
            Debug.LogError("[AvatarPlacer] avatarPrefab not assigned!");
            return;
        }

        previewAvatar = Instantiate(avatarPrefab);

        // Scale BEFORE adding AutoWalkDriver so Awake() sees correct lossyScale
        previewAvatar.transform.localScale = Vector3.one * avatarScale;

        avatarAnimator = previewAvatar.GetComponentInChildren<Animator>();
        if (avatarAnimator == null)
        {
            avatarAnimator = previewAvatar.AddComponent<Animator>();
            Debug.Log("[AvatarPlacer] Added Animator at runtime.");
        }

        // CRITICAL: set Avatar for humanoid retargeting.
        // Without it the skeleton stays in T-pose while the body moves ("gliding hands straight").
        if (avatarAsset != null)
        {
            avatarAnimator.avatar = avatarAsset;
            Debug.Log("[AvatarPlacer] Avatar assigned ✅");
        }
        else
        {
            Debug.LogWarning("[AvatarPlacer] avatarAsset NOT assigned! " +
                             "In Project window expand casual-f-0170.fbx → drag Avatar sub-asset to AvatarPlacer.avatarAsset.");
        }

        if (animatorController != null)
            avatarAnimator.runtimeAnimatorController = animatorController;
        else
            Debug.LogWarning("[AvatarPlacer] animatorController not assigned — using prefab's controller.");

        avatarAnimator.applyRootMotion = false;
        // Freeze animator during placement so avatar stands still
        avatarAnimator.speed = 0f;
        avatarAnimator.SetFloat(SpeedHash, 0f);
        avatarAnimator.SetBool(GroundedHash, true);
        avatarAnimator.SetFloat(MotionSpeedHash, 0f);

        renderers         = previewAvatar.GetComponentsInChildren<Renderer>();
        originalMaterials = new Material[renderers.Length][];
        for (int i = 0; i < renderers.Length; i++)
            originalMaterials[i] = renderers[i].sharedMaterials;

        if (transparentMaterial != null) SetTransparent(true);

        // Kill ALL Rigidbodies (root + children) — they fight the CharacterController
        foreach (var rb2 in previewAvatar.GetComponentsInChildren<Rigidbody>(true))
        { rb2.isKinematic = true; rb2.useGravity = false; }

        // Disable all non-CC colliders on the avatar (bone hit-boxes, ragdoll, mesh colliders).
        // CharacterController inherits from Collider, so explicitly skip it.
        // These internal colliders physically block the CC capsule from inside.
        foreach (var col in previewAvatar.GetComponentsInChildren<Collider>(true))
            if (!(col is CharacterController))
                col.enabled = false;

        walkDriver           = previewAvatar.AddComponent<AutoWalkDriver>();
        walkDriver.MoveSpeed = walkSpeed;
        // Do NOT disable walkDriver — _walking=false already stops its Update early.
        // Disabling then re-enabling in the same frame caused CC state issues.

        previewAvatar.transform.position = new Vector3(0f, floorOffset, 2f);
        Debug.Log("[AvatarPlacer] Preview spawned.");
    }

    // ─────────────────────────────────────────────────────────────
    void Update()
    {
        if (!didSpawn)
        {
            if (obstaclePlacer == null || obstaclePlacer.AllPlaced)
            {
                SpawnPreview();
                didSpawn = true;
            }
            return;
        }

#if UNITY_EDITOR
        UpdateEditor();
#else
        UpdateXR();
#endif
    }

    // ── Camera-relative helper ────────────────────────────────────
    static void GetCamDirs(out Vector3 fwd, out Vector3 right)
    {
        var cam = Camera.main != null ? Camera.main.transform : null;
        fwd   = cam != null ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized : Vector3.forward;
        right = cam != null ? Vector3.ProjectOnPlane(cam.right,   Vector3.up).normalized : Vector3.right;
    }

    // ─────────────────────────────────────────────────────────────
    //  EDITOR
    // ─────────────────────────────────────────────────────────────
    void UpdateEditor()
    {
        if (Keyboard.current == null) return;
        var kb = Keyboard.current;

        if (!isPlaced)
        {
            if (previewAvatar == null) return;
            float h = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f)
                    + (kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.leftArrowKey.isPressed ? 1f : 0f);
            float v = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f)
                    + (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f);

            previewAvatar.transform.position += new Vector3(h, 0, v) * placeMoveSpeed * Time.deltaTime;
            var p = previewAvatar.transform.position; p.y = floorOffset;
            previewAvatar.transform.position = p;

            if (kb.qKey.isPressed) rotationAngle -= rotationSpeed * Time.deltaTime;
            if (kb.eKey.isPressed) rotationAngle += rotationSpeed * Time.deltaTime;
            previewAvatar.transform.rotation = Quaternion.Euler(0, rotationAngle, 0);

            if (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)
                PlaceAvatar();
        }
        else if (!goalSet)
        {
            if (_goalPreview == null) return;
            float h = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f)
                    + (kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.leftArrowKey.isPressed ? 1f : 0f);
            float v = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f)
                    + (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f);

            _goalPreview.transform.position += new Vector3(h, 0, v) * placeMoveSpeed * Time.deltaTime;
            var gp = _goalPreview.transform.position; gp.y = floorOffset + 0.15f;
            _goalPreview.transform.position = gp;

            if (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)
                PlaceGoal();
            // B in goal phase = confirm goal at current position AND start walking immediately
            if (kb.bKey.wasPressedThisFrame) { PlaceGoal(); if (goalSet) ToggleWalkAnim(); }
        }
        else
        {
            if (kb.bKey.wasPressedThisFrame) ToggleWalkAnim();
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  XR (Quest) — all movement camera-relative
    // ─────────────────────────────────────────────────────────────
    void UpdateXR()
    {
        GetCamDirs(out Vector3 fwd, out Vector3 right);
        Vector2 moveStick   = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick); // right
        Vector2 rotateStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);   // left

        if (!isPlaced)
        {
            if (previewAvatar == null) return;

            bool movedByRay = false;
            if (rightController != null)
            {
                // Preferred: point controller at floor
                Ray ray = new Ray(rightController.position, rightController.forward);
                if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
                {
                    previewAvatar.transform.position = new Vector3(hit.point.x,
                                                                    hit.point.y + floorOffset,
                                                                    hit.point.z);
                    movedByRay = true;
                }
            }

            if (!movedByRay)
            {
                // Fallback: right thumbstick camera-relative
                var p = previewAvatar.transform.position;
                p += right * moveStick.x * placeMoveSpeed * Time.deltaTime;
                p += fwd   * moveStick.y * placeMoveSpeed * Time.deltaTime;
                p.y = floorOffset;
                previewAvatar.transform.position = p;
            }

            // Left stick X = rotate avatar
            rotationAngle += rotateStick.x * rotationSpeed * Time.deltaTime;
            previewAvatar.transform.rotation = Quaternion.Euler(0, rotationAngle, 0);

            if (OVRInput.GetDown(OVRInput.Button.One)) PlaceAvatar(); // A
        }
        else if (!goalSet)
        {
            if (_goalPreview == null) return;
            // Camera-relative thumbstick to reposition yellow sphere
            var gp = _goalPreview.transform.position;
            gp += right * moveStick.x * placeMoveSpeed * Time.deltaTime;
            gp += fwd   * moveStick.y * placeMoveSpeed * Time.deltaTime;
            gp.y = floorOffset + 0.15f;
            _goalPreview.transform.position = gp;

            if (OVRInput.GetDown(OVRInput.Button.One)) PlaceGoal(); // A = confirm goal only
            // B = confirm goal at current position AND start walking immediately
            if (OVRInput.GetDown(OVRInput.Button.Two)) { PlaceGoal(); if (goalSet) ToggleWalkAnim(); }
        }
        else
        {
            if (OVRInput.GetDown(OVRInput.Button.Two)) ToggleWalkAnim(); // B = toggle walk/stop
        }
    }

    // ─────────────────────────────────────────────────────────────
    void PlaceAvatar()
    {
        isPlaced = true;
        if (transparentMaterial != null) SetTransparent(false);
        // Do NOT unfreeze animator yet — keep avatar still until B is pressed

        var fmv = FindObjectOfType<FloorMeshValidator>();
        if (fmv != null) fmv.avatarTransform = previewAvatar.transform;

        SpawnGoalPreview();
        Debug.Log("[AvatarPlacer] Start placed. Now position the goal (yellow sphere).");
    }

    void SpawnGoalPreview()
    {
        _goalPreview = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _goalPreview.name = "GoalPreview";
        Destroy(_goalPreview.GetComponent<Collider>());
        _goalPreview.transform.localScale = Vector3.one * 0.3f;

        // Start 4 m ahead of avatar
        Vector3 start = previewAvatar.transform.position;
        Vector3 ahead = previewAvatar.transform.forward * 4f;
        _goalPreview.transform.position = new Vector3(start.x + ahead.x,
                                                       floorOffset + 0.15f,
                                                       start.z + ahead.z);

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
        mat.color = new Color(1f, 0.85f, 0f, 0.85f); // yellow
        _goalPreview.GetComponent<Renderer>().material = mat;
    }

    void PlaceGoal()
    {
        _goalPos = new Vector3(_goalPreview.transform.position.x,
                               0f,
                               _goalPreview.transform.position.z);

        // Permanent cyan goal marker
        _goalMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _goalMarker.name = "GoalMarker";
        Destroy(_goalMarker.GetComponent<Collider>());
        _goalMarker.transform.localScale = Vector3.one * 0.35f;
        _goalMarker.transform.position   = new Vector3(_goalPos.x, floorOffset + 0.175f, _goalPos.z);

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
        mat.color = new Color(0.4f, 0.95f, 1f, 1f); // cyan
        _goalMarker.GetComponent<Renderer>().material = mat;

        Destroy(_goalPreview); _goalPreview = null;
        goalSet = true;

        // Preview the curved path on the floor immediately — visible before B is pressed
        if (walkDriver != null)
            walkDriver.PreviewPath(_goalPos);

        Debug.Log($"[AvatarPlacer] Goal placed at {_goalPos}. Path previewed. Press B to walk.");
    }

    void ToggleWalkAnim()
    {
        Debug.Log($"[AvatarPlacer] ToggleWalkAnim called — goalSet={goalSet}, walkDriver={walkDriver != null}, isWalking={isWalking}");
        if (!goalSet || walkDriver == null) return;
        isWalking = !isWalking;

        if (isWalking)
        {
            // Unfreeze animator now that walking starts
            if (avatarAnimator != null) avatarAnimator.speed = 1f;
            walkDriver.StartWalk(_goalPos);
        }
        else
        {
            walkDriver.StopWalk();
            // Re-freeze so avatar stands still
            if (avatarAnimator != null) avatarAnimator.speed = 0f;
        }

        Debug.Log($"[AvatarPlacer] Walking = {isWalking}");
    }

    // ─────────────────────────────────────────────────────────────
    void SetTransparent(bool transparent)
    {
        for (int i = 0; i < renderers.Length; i++)
        {
            if (transparent)
            {
                var mats = new Material[originalMaterials[i].Length];
                for (int j = 0; j < mats.Length; j++)
                {
                    mats[j] = new Material(transparentMaterial);
                    var orig = originalMaterials[i][j];
                    if (orig != null && orig.HasProperty("_BaseMap"))
                        mats[j].SetTexture("_BaseMap", orig.GetTexture("_BaseMap"));
                    Color c = mats[j].color; c.a = previewAlpha; mats[j].color = c;
                }
                renderers[i].materials = mats;
            }
            else
            {
                renderers[i].sharedMaterials = originalMaterials[i];
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Reset
    // ─────────────────────────────────────────────────────────────
    public void ResetAvatarOnly()
    {
        DestroyAvatarObjects();
        didSpawn = true;
        SpawnPreview();
    }

    public void FullReset()
    {
        DestroyAvatarObjects();
        didSpawn = false;
    }

    void DestroyAvatarObjects()
    {
        if (_goalPreview != null) { Destroy(_goalPreview); _goalPreview = null; }
        if (_goalMarker  != null) { Destroy(_goalMarker);  _goalMarker  = null; }

        if (walkDriver != null) walkDriver.Cleanup();
        if (previewAvatar != null) { Destroy(previewAvatar); previewAvatar = null; }

        var fmv = FindObjectOfType<FloorMeshValidator>();
        if (fmv != null) fmv.avatarTransform = null;

        avatarAnimator    = null;
        walkDriver        = null;
        renderers         = null;
        originalMaterials = null;
        isPlaced          = false;
        isWalking         = false;
        goalSet           = false;
        rotationAngle     = 0f;
        _goalPos          = Vector3.zero;
    }

    // ─────────────────────────────────────────────────────────────
    //  HUD
    // ─────────────────────────────────────────────────────────────
    void OnGUI()
    {
        if (!didSpawn) return;

        string msg = null;
#if UNITY_EDITOR
        if (!isPlaced)
            msg = "Place Start Point\nWASD = move  |  Q/E = rotate  |  SPACE = confirm";
        else if (!goalSet)
            msg = "Place Goal (yellow sphere)\nWASD = move  |  SPACE = confirm only  |  B = confirm + walk";
        else if (!isWalking)
            msg = "Press B to start walking toward goal";
        else
            msg = "Walking...  press B to stop";
#else
        if (!isPlaced)
            msg = "Place Avatar\nRight stick = move  |  Left stick = rotate  |  A = confirm";
        else if (!goalSet)
            msg = "Place Goal (yellow sphere)\nRight stick = move  |  A = confirm only  |  B = confirm + walk";
        else if (!isWalking)
            msg = "Press B to start walking";
        else
            msg = "Walking...  press B to stop";
#endif
        if (msg == null) return;

        var style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = Color.white;
        GUI.Box(new Rect(Screen.width * 0.5f - 220f, 12f, 440f, 52f), msg, style);
    }

    // ─────────────────────────────────────────────────────────────
    public Animator  GetAvatarAnimator() => avatarAnimator;
    public Transform GetAvatarRoot()     => previewAvatar != null ? previewAvatar.transform : null;
}
