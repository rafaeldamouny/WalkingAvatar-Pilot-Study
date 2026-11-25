using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

#if !UNITY_EDITOR
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Manages the 12-layout experiment session.
///
/// Setup in Inspector:
///   • Assign avatarPrefab (your casual-f-0170 prefab)
///   • Assign corridorRoot (parent of walls/floor — moved for corridor adjustment)
///   • Assign chairPrefab  (a simple 0.60×1.00×0.70 box or visual obstacle)
///   • Assign chairContainer (child of corridorRoot, chairs instantiated here)
///   • Assign avatarSpawnPoint (Transform at corridor entrance center)
///
/// Usage flow:
///   1. Set ParticipantId via ExperimentMenu
///   2. Call BeginSession()  → randomizes 12-trial order
///   3. Call LoadNextLayout() → places chairs + path for current trial
///   4. Call StartTrial()    → avatar walks
///   5. Avatar fires OnGoalReached → TrialManager records, calls OnTrialComplete
///   6. Repeat until all 12 done → session complete
/// </summary>
public class TrialManager : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────
    [Header("Prefabs & Scene References")]
    public GameObject   avatarPrefab;
    public Transform    corridorRoot;        // moved for position calibration
    public GameObject   chairPrefab;         // box obstacle 0.60 × 1.00 × 0.70 m
    public Transform    chairContainer;      // child of corridorRoot
    public Transform    avatarSpawnPoint;    // world-space start position

    [Header("Avatar")]
    public Avatar       avatarRigAsset;      // humanoid rig asset for retargeting
    public RuntimeAnimatorController animController;
    public float        walkSpeed = 1.0f;
    [Tooltip("Uniform scale applied to avatar after spawn. Increase if avatar looks like a child (e.g. FBX imported at wrong units). Try 100 if model was made in centimetres.")]
    public float        avatarScale = 1.0f;

    [Header("Corridor Geometry (read-only reference)")]
    [Tooltip("Corridor width for validation. Set to 1.80 m.")]
    public float corridorWidth = 1.80f;
    [Tooltip("Opaque material used by the two physical-corridor alignment walls.")]
    public Material wallMaterial;

    const float CorridorLength = 48.4f;
    const float CorridorWallHeight = 2.5f;
    const float CorridorWallThickness = 0.10f;

    [Header("Obstacle Dimensions")]
    public float chairWidth  = 0.60f;   // X
    public float chairHeight = 1.00f;   // Y
    public float chairDepth  = 0.70f;   // Z

    // ── State ─────────────────────────────────────────────────────
    public int  ParticipantId { get; set; } = 1;
    public int  CurrentTrialIndex { get; private set; } = -1;  // 0-based position in trialOrder
    public int  CurrentLayout    { get; private set; } = -1;   // 1-based layout number (1–12)
    public bool SessionActive    { get; private set; } = false;
    public bool TrialRunning     { get; private set; } = false;
    public bool FloorReady       { get; private set; } = false;

    int[]            _trialOrder = new int[12];   // layout numbers 1-12, shuffled
    float            _trialStartTime;
    List<TrialRecord> _records = new List<TrialRecord>();

    GameObject      _avatarInstance;
    SimpleWalkDriver _driver;
    List<GameObject> _chairs = new List<GameObject>();

    // ── Events ────────────────────────────────────────────────────
    public event Action<int, int> OnTrialLoaded;    // (trialIndex, layoutNum)
    public event Action<int>      OnTrialComplete;  // (trialIndex)
    public event Action           OnSessionComplete;

    // ── JSON helpers ──────────────────────────────────────────────
    [Serializable] class ChairEntry  { public int chair; public float cx, cz; public string pass; public float target; }
    [Serializable] class ChairList   { public List<ChairEntry> items; }
    [Serializable] class PathPoint   { public float x, y, z; }

    void Awake()
    {
        EnsureCorridorWalls();
    }

    IEnumerator Start()
    {
#if UNITY_EDITOR
        FloorReady = true;
        yield break;
#else
        // Never show the layout at its temporary tracking-origin height.
        SetRenderersEnabled(corridorRoot.gameObject, false);

        // Use the same MRUK floor anchor as the room/floor visualization. Moving
        // CorridorRoot also moves its walls, floor sections, goal and chair parent.
        while (MRUK.Instance == null) yield return null;

        MRUKRoom room = null;
        while (room == null)
        {
            room = MRUK.Instance.GetCurrentRoom();
            yield return null;
        }

        MRUKAnchor floor = null;
        while (floor == null)
        {
            floor = room.FloorAnchor;
            yield return null;
        }

        // Get the actual plane surface under the headset. GetAnchorCenter can be
        // inappropriate for anchors that also contain volume bounds; a plane hit
        // is the physical floor surface we need.
        Vector3 rayXZ = Camera.main != null ? Camera.main.transform.position : floor.transform.position;
        Vector3 rayOrigin = new Vector3(rayXZ.x, rayXZ.y + 10f, rayXZ.z);
        float mrukFloorY;
        if (floor.Raycast(new Ray(rayOrigin, Vector3.down), 100f, out RaycastHit floorHit,
                          MRUKAnchor.ComponentType.Plane))
        {
            mrukFloorY = floorHit.point.y;
        }
        else
        {
            // A floor plane anchor's transform lies on the plane. This fallback
            // is still safer than using the center of optional volume bounds.
            mrukFloorY = floor.transform.position.y;
            Debug.LogWarning("[TrialManager] Down-ray missed MRUK floor boundary; using floor anchor transform Y.");
        }

        // A physical floor must be below the headset and within a plausible room
        // height. Reject stale/misregistered anchors (the failure that placed the
        // layout one storey above). The rig is configured for FloorLevel tracking,
        // where world Y=0 is the Quest-calibrated physical floor.
        float headY = rayXZ.y;
        bool mrukFloorIsPlausible = mrukFloorY < headY - 0.20f && headY - mrukFloorY < 3.5f;
        float detectedFloorY = mrukFloorIsPlausible ? mrukFloorY : 0f;
        if (!mrukFloorIsPlausible)
            Debug.LogError($"[TrialManager] Rejected invalid MRUK floor Y={mrukFloorY:F3} " +
                           $"for headset Y={headY:F3}; using Quest FloorLevel Y=0.");

        Vector3 rootPosition = corridorRoot.position;
        rootPosition.y = detectedFloorY;
        corridorRoot.position = rootPosition;
        NormalizeCorridorLocalHeights();
        FloorReady = true;

        // Let any floor/wall visualizer finish creating its children first.
        yield return null;
        HideRoomDebugVisuals();
        SetRenderersEnabled(corridorRoot.gameObject, _virtualVisible);

        // If a layout was loaded unusually quickly, rebuild it at the corrected Y.
        if (CurrentLayout >= 1 && !TrialRunning)
            ReloadCurrentLayout();

        Debug.Log($"[TrialManager] Experiment aligned to MRUK floor surface Y={detectedFloorY:F3}; " +
                  $"anchorY={floor.transform.position.y:F3}, cameraY={rayXZ.y:F3}.");
#endif
    }

    void NormalizeCorridorLocalHeights()
    {
        // Remove any accidental Inspector Y edits. These values are offsets above
        // the physical floor, not world-space heights.
        SetChildLocalY("ChairContainer", 0f);
        SetChildLocalY("AvatarSpawnPoint", 0f);
        SetChildLocalY("Floor_FreeStart", 0.003f);
        SetChildLocalY("Floor_Obstacles", 0.003f);
        SetChildLocalY("Floor_FreeEnd", 0.003f);
        SetChildLocalY("GoalMarker", 0.003f);
        SetChildLocalY("Wall_Left", CorridorWallHeight * 0.5f);
        SetChildLocalY("Wall_Right", CorridorWallHeight * 0.5f);
    }

    void SetChildLocalY(string childName, float localY)
    {
        Transform child = corridorRoot.Find(childName);
        if (child == null) return;
        Vector3 position = child.localPosition;
        position.y = localY;
        child.localPosition = position;
    }

    void HideRoomDebugVisuals()
    {
        // Preserve colliders, but hide MRUK/test visualization geometry.
        foreach (var validator in FindObjectsByType<FloorMeshValidator>(FindObjectsSortMode.None))
            validator.SetVisible(false);

        foreach (var detector in FindObjectsByType<WallDetector>(FindObjectsSortMode.None))
            detector.SetOverlayVisible(false);

        foreach (var renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            string n = renderer.gameObject.name;
            if (n == "FloorDebugCube" || n == "FloorFill" || n == "FloorGrid")
                renderer.enabled = false;
        }
    }

    /// <summary>
    /// The current experiment scene has previously been saved without its wall
    /// children. Recreate either missing wall before the first headset frame so
    /// the APK cannot silently contain chairs/avatar but no alignment geometry.
    /// </summary>
    void EnsureCorridorWalls()
    {
        if (corridorRoot == null)
        {
            Debug.LogError("[TrialManager] CorridorRoot is not assigned; corridor walls cannot be created.");
            return;
        }

        CreateWallIfMissing(
            "Wall_Left",
            new Vector3(-CorridorWallThickness * 0.5f, CorridorWallHeight * 0.5f, CorridorLength * 0.5f));
        CreateWallIfMissing(
            "Wall_Right",
            new Vector3(corridorWidth + CorridorWallThickness * 0.5f, CorridorWallHeight * 0.5f, CorridorLength * 0.5f));
    }

    void CreateWallIfMissing(string wallName, Vector3 localPosition)
    {
        if (corridorRoot.Find(wallName) != null) return;

        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = wallName;
        wall.transform.SetParent(corridorRoot, false);
        wall.transform.localPosition = localPosition;
        wall.transform.localRotation = Quaternion.identity;
        wall.transform.localScale = new Vector3(CorridorWallThickness, CorridorWallHeight, CorridorLength);

        var renderer = wall.GetComponent<MeshRenderer>();
        if (wallMaterial != null)
            renderer.sharedMaterial = wallMaterial;
        else
            Debug.LogError($"[TrialManager] {wallName} was created, but Wall Material is not assigned.");

        renderer.enabled = true;
        wall.SetActive(true);
        Debug.Log($"[TrialManager] Recreated missing {wallName}: localPosition={localPosition}, " +
                  $"scale={wall.transform.localScale}, material={renderer.sharedMaterial?.name ?? "NULL"}.");
    }

    // ─────────────────────────────────────────────────────────────
    //  PUBLIC API
    // ─────────────────────────────────────────────────────────────

    /// <summary>Shuffle the 12 layouts and prepare for trial 0.</summary>
    public void BeginSession()
    {
        _records.Clear();
        _trialOrder = GenerateOrder(ParticipantId);
        CurrentTrialIndex = -1;
        SessionActive     = true;
        TrialRunning      = false;
        Debug.Log($"[TrialManager] Session started. PID={ParticipantId}  Order={string.Join(",", _trialOrder)}");
    }

    /// <summary>
    /// Load the next trial's chairs and avatar path.
    /// Returns false if all 12 trials are done.
    /// </summary>
    public bool LoadNextLayout()
    {
        if (!SessionActive) { Debug.LogWarning("[TrialManager] Call BeginSession() first."); return false; }

        CurrentTrialIndex++;
        if (CurrentTrialIndex >= 12)
        {
            FinishSession();
            return false;
        }

        CurrentLayout = _trialOrder[CurrentTrialIndex];
        LoadLayout(CurrentLayout);
        OnTrialLoaded?.Invoke(CurrentTrialIndex, CurrentLayout);
        Debug.Log($"[TrialManager] Loaded trial {CurrentTrialIndex + 1}/12  layout P{CurrentLayout:D2}");
        return true;
    }

    /// <summary>Reload the same layout (for re-running a failed trial).</summary>
    public void ReloadCurrentLayout()
    {
        if (CurrentLayout < 1) return;
        LoadLayout(CurrentLayout);
    }

    /// <summary>Start avatar walking for the current trial.</summary>
    public void StartTrial()
    {
        if (_driver == null) { Debug.LogWarning("[TrialManager] No avatar loaded."); return; }
        _trialStartTime = Time.time;
        TrialRunning    = true;
        _driver.StartWalk();
        Debug.Log($"[TrialManager] Trial started  P{CurrentLayout:D2}");
    }

    /// <summary>Stop the trial early (experimenter abort).</summary>
    public void AbortTrial()
    {
        if (!TrialRunning) return;
        TrialRunning = false;
        _driver?.StopWalk();
        RecordTrial(completed: false);
        Debug.Log($"[TrialManager] Trial aborted  P{CurrentLayout:D2}");
    }

    /// <summary>Reset avatar to path start without clearing chairs (redo).</summary>
    public void RedoCurrentTrial()
    {
        _driver?.StopWalk();
        _driver?.ResetToStart();
        TrialRunning = false;
    }

    /// <summary>
    /// Returns the current trial order as a displayable string.
    /// </summary>
    public string GetOrderString() =>
        string.Join(" → ", System.Array.ConvertAll(_trialOrder, n => $"P{n:D2}"));

    // ─────────────────────────────────────────────────────────────
    //  INTERNAL
    // ─────────────────────────────────────────────────────────────

    void LoadLayout(int layoutNum)
    {
        StopAllCoroutines();
        TrialRunning = false;

        // ── Clear old chairs ──────────────────────────────────────
        foreach (var c in _chairs) if (c != null) Destroy(c);
        _chairs.Clear();

        // ── Destroy old avatar ────────────────────────────────────
        if (_avatarInstance != null) Destroy(_avatarInstance);
        _driver = null;

        // ── Spawn new avatar ──────────────────────────────────────
        Vector3 spawnPos = avatarSpawnPoint != null
            ? avatarSpawnPoint.position
            : new Vector3(corridorWidth / 2f, 0f, 0f);

        _avatarInstance = Instantiate(avatarPrefab, spawnPos, Quaternion.identity);
        // Fix avatar size if model was imported at wrong scale
        if (!Mathf.Approximately(avatarScale, 1f))
            _avatarInstance.transform.localScale = Vector3.one * avatarScale;
        PrepareAvatar(_avatarInstance);

        // ── Load path ─────────────────────────────────────────────
        var pathPts = LoadPath(layoutNum);
        if (pathPts != null && pathPts.Count > 0)
        {
            // Apply the full detected-floor offset and corridor rotation.
            var offset   = corridorRoot != null ? corridorRoot.position : Vector3.zero;
            var rotation = corridorRoot != null ? corridorRoot.rotation : Quaternion.identity;
            for (int i = 0; i < pathPts.Count; i++)
                pathPts[i] = rotation * pathPts[i] + offset;

            _driver.SetPath(pathPts);

            // Teleport avatar to path start (direct assignment — no CharacterController needed)
            if (pathPts.Count > 0)
                _avatarInstance.transform.position = pathPts[0];
        }

        // ── Load chairs ───────────────────────────────────────────
        var chairEntries = LoadChairs(layoutNum);
        if (chairEntries != null)
        {
            var offset   = corridorRoot != null ? corridorRoot.position : Vector3.zero;
            var rotation = corridorRoot != null ? corridorRoot.rotation : Quaternion.identity;
            foreach (var e in chairEntries)
            {
                Vector3 localPos = new Vector3(e.cx, chairHeight / 2f, e.cz);
                Vector3 pos      = rotation * localPos + offset;
                Vector3 scale = new Vector3(chairWidth, chairHeight, chairDepth);
                var go = SpawnChair(pos, scale);
                _chairs.Add(go);
            }
        }

        Debug.Log($"[TrialManager] Layout P{layoutNum:D2} ready: {_chairs.Count} chairs, {pathPts?.Count ?? 0} path pts.");

        // Re-apply current visibility state to newly spawned chairs
        if (!_virtualVisible)
            foreach (var c in _chairs)
                SetRenderersEnabled(c, false);
    }

    // ── Passthrough visibility toggle ─────────────────────────────
    bool _virtualVisible = true;
    public bool VirtualVisible => _virtualVisible;

    /// <summary>
    /// Show or hide all virtual corridor objects (walls, floor, chairs, zone markers).
    /// The avatar is never affected — stays visible at all times.
    /// </summary>
    public void SetVirtualVisible(bool visible)
    {
        _virtualVisible = visible;

        // Corridor root: walls, floor, zone markers
        if (corridorRoot != null)
            SetRenderersEnabled(corridorRoot.gameObject, visible);

        // Chairs (parented inside corridorRoot but toggled explicitly in case
        // corridorRoot renderer loop was already handled above)
        foreach (var c in _chairs)
            SetRenderersEnabled(c, visible);
    }

    static void SetRenderersEnabled(GameObject root, bool visible)
    {
        if (root == null) return;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            r.enabled = visible;
    }

    void PrepareAvatar(GameObject avatar)
    {
        // Kill rigidbodies — avatar uses direct transform movement, not physics
        foreach (var rb in avatar.GetComponentsInChildren<Rigidbody>(true))
        { rb.isKinematic = true; rb.useGravity = false; }

        // Disable CharacterController — SimpleWalkDriver uses transform.position directly
        foreach (var cc in avatar.GetComponentsInChildren<CharacterController>(true))
            cc.enabled = false;

        // Disable all other colliders (don't want the avatar mesh blocking CC or raycasts)
        foreach (var col in avatar.GetComponentsInChildren<Collider>(true))
            col.enabled = false;

        // Animator
        var anim = avatar.GetComponentInChildren<Animator>() ?? avatar.AddComponent<Animator>();
        anim.applyRootMotion = false;
        if (animController != null) anim.runtimeAnimatorController = animController;
        if (avatarRigAsset  != null) anim.avatar = avatarRigAsset;

        // SimpleWalkDriver
        _driver = avatar.AddComponent<SimpleWalkDriver>();
        _driver.MoveSpeed = walkSpeed;
        _driver.OnGoalReached += HandleGoalReached;
    }

    GameObject SpawnChair(Vector3 worldPos, Vector3 scale)
    {
        GameObject go;
        if (chairPrefab != null)
        {
            go = Instantiate(chairPrefab, worldPos, Quaternion.identity,
                             chairContainer != null ? chairContainer : corridorRoot);
            go.transform.localScale = scale;
        }
        else
        {
            // Fallback: create a primitive cube
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(chairContainer != null ? chairContainer : corridorRoot);
            go.transform.position   = worldPos;
            go.transform.localScale = scale;

            // Simple dark-grey material
            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ??
                                       Shader.Find("Standard"));
                mat.color = new Color(0.25f, 0.20f, 0.15f);
                rend.material = mat;
            }
        }
        go.name = $"Chair_{worldPos.z:F1}";

        // Chairs are visual only — the avatar follows pre-authored paths that already
        // route around them. Disabling colliders prevents the CharacterController
        // from clipping/getting stuck against chair geometry.
        foreach (var col in go.GetComponentsInChildren<Collider>(true))
            col.enabled = false;

        return go;
    }

    void HandleGoalReached()
    {
        if (!TrialRunning) return;
        TrialRunning = false;
        RecordTrial(completed: true);
        int idx = CurrentTrialIndex;
        OnTrialComplete?.Invoke(idx);
        Debug.Log($"[TrialManager] Trial {idx + 1}/12 complete  P{CurrentLayout:D2}  t={_records[_records.Count - 1].durationSec:F1}s");
    }

    void RecordTrial(bool completed)
    {
        _records.Add(new TrialRecord {
            participantId = ParticipantId,
            trialIndex    = CurrentTrialIndex + 1,
            layoutNum     = CurrentLayout,
            completedOk   = completed,
            durationSec   = Time.time - _trialStartTime,
            timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    void FinishSession()
    {
        SessionActive = false;
        SaveLog();
        OnSessionComplete?.Invoke();
        Debug.Log("[TrialManager] Session complete — log saved.");
    }

    // ── Counterbalanced order ─────────────────────────────────────
    // Uses participant ID as seed for reproducible randomization.
    static int[] GenerateOrder(int participantId)
    {
        int[] order = new int[12];
        for (int i = 0; i < 12; i++) order[i] = i + 1;

        // Fisher-Yates shuffle seeded by participant ID
        var rng = new System.Random(participantId * 7919);
        for (int i = 11; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            int tmp = order[i]; order[i] = order[j]; order[j] = tmp;
        }
        return order;
    }

    // ── JSON loaders ──────────────────────────────────────────────
    List<Vector3> LoadPath(int layoutNum)
    {
        string key = $"Paths/P{layoutNum:D2}_avatar_path";
        var ta = Resources.Load<TextAsset>(key);
        if (ta == null) { Debug.LogError($"[TrialManager] Missing resource: {key}"); return null; }

        var raw = JsonHelper.FromJsonArray<PathPoint>(ta.text);
        var pts = new List<Vector3>(raw.Length);
        foreach (var p in raw) pts.Add(new Vector3(p.x, p.y, p.z));
        return pts;
    }

    List<ChairEntry> LoadChairs(int layoutNum)
    {
        string key = $"Layouts/P{layoutNum:D2}_chairs";
        var ta = Resources.Load<TextAsset>(key);
        if (ta == null) { Debug.LogError($"[TrialManager] Missing resource: {key}"); return null; }

        var arr = JsonHelper.FromJsonArray<ChairEntry>(ta.text);
        return new List<ChairEntry>(arr);
    }

    // ── Logging ───────────────────────────────────────────────────
    void SaveLog()
    {
        string dir  = Application.persistentDataPath;
        string file = Path.Combine(dir, $"trial_log_P{ParticipantId:D3}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("participant_id,trial_num,layout,completed,duration_sec,timestamp");
        foreach (var r in _records)
            sb.AppendLine($"{r.participantId},{r.trialIndex},{r.layoutNum},{r.completedOk},{r.durationSec:F2},{r.timestamp}");

        File.WriteAllText(file, sb.ToString());
        Debug.Log($"[TrialManager] Log saved → {file}");
    }

    // ── Data structures ───────────────────────────────────────────
    struct TrialRecord
    {
        public int    participantId;
        public int    trialIndex;
        public int    layoutNum;
        public bool   completedOk;
        public float  durationSec;
        public string timestamp;
    }
}

/// <summary>
/// Unity's JsonUtility doesn't support top-level arrays.
/// Wrap them in a temporary object to deserialize.
/// </summary>
public static class JsonHelper
{
    public static T[] FromJsonArray<T>(string json)
    {
        string wrapped = "{\"items\":" + json + "}";
        var wrapper = JsonUtility.FromJson<Wrapper<T>>(wrapped);
        return wrapper.items;
    }

    [Serializable] class Wrapper<T> { public T[] items; }
}
