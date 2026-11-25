using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Follows a pre-authored waypoint path via direct transform movement.
/// NO CharacterController, NO physics, NO gravity.
/// Avatar Y is clamped to path Y every frame → stays exactly on floor, never falls.
/// Call SetPath() then StartWalk().
/// </summary>
public class SimpleWalkDriver : MonoBehaviour
{
    [Header("Movement")]
    public float MoveSpeed       = 1.0f;
    public float SpeedChangeRate = 5.0f;
    public float TurnSpeed       = 120f;

    [Header("Waypoint Following")]
    public float GoalRadius    = 0.50f;    // final goal tolerance
    public float WpRadius      = 0.10f;    // per-waypoint advance threshold
    public float LookaheadDist = 0.60f;    // steer toward point this far ahead on path

    [Header("Animation")]
    public float AnimWalkSpeed = 1f;       // value sent to Speed parameter while walking

    [Header("Path Trace")]
    public float LineWidth    = 0.04f;
    public Color WalkedColor  = new Color(0.15f, 1f, 0.30f, 0.9f);
    public Color PlannedColor = new Color(1f,   0.85f, 0.10f, 0.9f);

    // ── Anim hashes ───────────────────────────────────────────────
    static readonly int SpeedHash  = Animator.StringToHash("Speed");
    static readonly int GroundHash = Animator.StringToHash("Grounded");
    static readonly int FallHash   = Animator.StringToHash("FreeFall");

    // ── State ─────────────────────────────────────────────────────
    Animator _anim;
    float _baseAnimatorPlaybackSpeed = 1f;
    bool     _hasAnim;
    bool     _hasSpeed, _hasGround, _hasFall;
    float    _speed;

    List<Vector3> _path    = new List<Vector3>();
    int           _wpIndex = 0;
    bool          _walking = false;
    float         _stuckTimer;
    int           _prevWpIndex;

    LineRenderer  _lrWalked, _lrPlanned;
    List<Vector3> _walked     = new List<Vector3>();
    Vector3       _lastSample;

    public bool  IsWalking => _walking;
    public float Progress  => _path.Count < 2 ? 0f : (float)_wpIndex / (_path.Count - 1);

    // ── Events ────────────────────────────────────────────────────
    public System.Action OnGoalReached;

    // ─────────────────────────────────────────────────────────────
    void Awake()
    {
        // Disable CharacterController if present — we do NOT use physics.
        // Avatar stays at Y=pathY every frame via direct transform assignment.
        var cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        // Also check parents (avatar rig sometimes has CC on root)
        foreach (var c in GetComponentsInParent<CharacterController>(true))
            c.enabled = false;

        // Animator
        _hasAnim = TryGetComponent(out _anim);
        if (!_hasAnim) _hasAnim = (_anim = GetComponentInChildren<Animator>()) != null;
        if (_hasAnim)
        {
            _anim.applyRootMotion = false;
            // Preserve the playback rate configured by SeatedWalkingPlatform for
            // the Kevin walk clip. Resetting this to 1 here overrode its speed sync.
            _baseAnimatorPlaybackSpeed = Mathf.Max(0.01f, _anim.speed);
        }
        if (_hasAnim)
            foreach (var parameter in _anim.parameters)
            {
                _hasSpeed |= parameter.nameHash == SpeedHash && parameter.type == AnimatorControllerParameterType.Float;
                _hasGround |= parameter.nameHash == GroundHash && parameter.type == AnimatorControllerParameterType.Bool;
                _hasFall |= parameter.nameHash == FallHash && parameter.type == AnimatorControllerParameterType.Bool;
            }

        _lrWalked  = MakeLR("SW_Walked",  WalkedColor,  LineWidth);
        _lrPlanned = MakeLR("SW_Planned", PlannedColor, LineWidth * 1.4f);
    }

    void Update()
    {
        if (!_walking) return;
        MoveStep();
        RecordWalked();
    }

    // ── Path following ────────────────────────────────────────────
    void MoveStep()
    {
        if (_wpIndex >= _path.Count) { ReachedGoal(); return; }

        _prevWpIndex = _wpIndex;

        // Advance past already-reached waypoints
        Vector3 toWp = Flat(_path[_wpIndex] - transform.position);
        while (toWp.magnitude < WpRadius && _wpIndex < _path.Count - 1)
        {
            _wpIndex++;
            toWp = Flat(_path[_wpIndex] - transform.position);
        }

        // Final goal check
        if (_wpIndex == _path.Count - 1 && toWp.magnitude < GoalRadius)
        { ReachedGoal(); return; }

        // Lookahead steering
        Vector3 lookahead = GetLookahead(LookaheadDist);
        Vector3 steer     = Flat(lookahead - transform.position);
        if (steer.sqrMagnitude > 0.01f)
        {
            float targetY = Vector3.SignedAngle(Vector3.forward, steer.normalized, Vector3.up);
            transform.rotation = Quaternion.Euler(0f,
                Mathf.MoveTowardsAngle(transform.eulerAngles.y, targetY,
                                       TurnSpeed * Time.deltaTime), 0f);
        }

        // Smooth speed toward target
        _speed = Mathf.Lerp(_speed, MoveSpeed, Time.deltaTime * SpeedChangeRate);

        // Horizontal movement only — Y comes from the path (= 0 for flat corridor)
        Vector3 pos = transform.position;
        pos += transform.forward * _speed * Time.deltaTime;

        // Pin Y to path waypoint — avatar is ALWAYS exactly on the floor.
        pos.y = _path[Mathf.Min(_wpIndex, _path.Count - 1)].y;

        transform.position = pos;

        // Stuck recovery — if waypoint index doesn't advance for 1.5 s, skip ahead
        if (_wpIndex == _prevWpIndex)
        {
            _stuckTimer += Time.deltaTime;
            if (_stuckTimer > 1.5f)
            {
                _stuckTimer = 0f;
                _wpIndex = Mathf.Min(_wpIndex + 8, _path.Count - 1);
                Debug.LogWarning($"[SimpleWalkDriver] Stuck → skipping to wp {_wpIndex}");
            }
        }
        else _stuckTimer = 0f;

        // Animation
        if (_hasAnim)
        {
            _anim.speed = _speed > 0.05f ? _baseAnimatorPlaybackSpeed : 0f;
            if (_hasSpeed) _anim.SetFloat(SpeedHash, _speed > 0.05f ? AnimWalkSpeed : 0f);
            if (_hasGround) _anim.SetBool(GroundHash, true);
            if (_hasFall) _anim.SetBool(FallHash, false);
        }
    }

    Vector3 GetLookahead(float dist)
    {
        float   rem  = dist;
        Vector3 from = transform.position;
        for (int i = _wpIndex; i < _path.Count; i++)
        {
            float seg = Flat(_path[i] - from).magnitude;
            if (seg >= rem)
            {
                Vector3 d = Flat(_path[i] - from).normalized;
                return new Vector3(from.x + d.x * rem, _path[i].y, from.z + d.z * rem);
            }
            rem -= seg; from = _path[i];
        }
        return _path[_path.Count - 1];
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    void ReachedGoal()
    {
        StopWalk();
        Debug.Log("[SimpleWalkDriver] Goal reached.");
        OnGoalReached?.Invoke();
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Load pre-authored waypoints. Draws the planned path immediately.</summary>
    public void SetPath(List<Vector3> waypoints)
    {
        _path = waypoints ?? new List<Vector3>();
        ShowPlanned();
        Debug.Log($"[SimpleWalkDriver] Path set: {_path.Count} waypoints.");
    }

    public void StartWalk()
    {
        if (_path == null || _path.Count < 2)
        { Debug.LogWarning("[SimpleWalkDriver] No path set — call SetPath() first."); return; }

        _wpIndex    = 0;
        _walking    = true;
        _stuckTimer = 0f;
        _lastSample = transform.position;
        _speed      = 0f;
        _walked.Clear();
        if (_lrWalked != null) _lrWalked.positionCount = 0;
        if (_hasAnim) _anim.speed = _baseAnimatorPlaybackSpeed;

        Debug.Log($"[SimpleWalkDriver] StartWalk ({_path.Count} pts).");
    }

    public void StopWalk()
    {
        _walking = false;
        _speed   = 0f;
        if (_hasAnim)
        {
            _anim.speed = 0f;
            if (_hasSpeed) _anim.SetFloat(SpeedHash, 0f);
            if (_hasGround) _anim.SetBool(GroundHash, true);
            if (_hasFall) _anim.SetBool(FallHash, false);
        }
    }

    public void ResumeWalk()
    {
        if (_path.Count < 2 || _wpIndex >= _path.Count) return;
        _walking = true;
        _stuckTimer = 0f;
        if (_hasAnim) _anim.speed = _baseAnimatorPlaybackSpeed;
    }

    /// <summary>Reset to start of current path without clearing chairs (redo).</summary>
    public void ResetToStart()
    {
        StopWalk();
        if (_path != null && _path.Count > 0)
            transform.position = _path[0];
        _walked.Clear();
        if (_lrWalked != null) _lrWalked.positionCount = 0;
        _wpIndex = 0;
    }

    public void ClearPath()
    {
        StopWalk();
        _path.Clear();
        if (_lrPlanned != null) _lrPlanned.positionCount = 0;
        if (_lrWalked  != null) _lrWalked.positionCount  = 0;
    }

    // ── Visualisation ─────────────────────────────────────────────
    void ShowPlanned()
    {
        if (_lrPlanned == null || _path.Count == 0) return;
        _lrPlanned.positionCount = _path.Count;
        var arr = new Vector3[_path.Count];
        for (int i = 0; i < _path.Count; i++)
            arr[i] = _path[i] + Vector3.up * 0.03f;
        _lrPlanned.SetPositions(arr);
    }

    void RecordWalked()
    {
        Vector3 p = transform.position + Vector3.up * 0.04f;
        if (Vector3.Distance(p, _lastSample) < 0.06f) return;
        _lastSample = p;
        _walked.Add(p);
        _lrWalked.positionCount = _walked.Count;
        _lrWalked.SetPositions(_walked.ToArray());
    }

    LineRenderer MakeLR(string goName, Color col, float w)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(null);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace     = true;
        lr.positionCount     = 0;
        lr.startWidth        = lr.endWidth = w;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        var s = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        var m = new Material(s) { color = col };
        m.SetFloat("_Surface", 1f);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.renderQueue = 4000;
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        lr.material = m;
        return lr;
    }

    void OnDestroy()
    {
        if (_lrWalked  != null) Destroy(_lrWalked.gameObject);
        if (_lrPlanned != null) Destroy(_lrPlanned.gameObject);
    }

    // ── HUD ───────────────────────────────────────────────────────
    void OnGUI()
    {
        string msg = _walking
            ? $"WALKING  wp {_wpIndex}/{_path.Count}  spd:{_speed:F2}  Y:{transform.position.y:F3}"
            : $"STOPPED  ({_path.Count} pts)  Y:{transform.position.y:F3}";
        var style = new GUIStyle(GUI.skin.box) { fontSize = 14 };
        style.normal.textColor = _walking ? new Color(0.1f, 1f, 0.3f) : new Color(1f, 0.65f, 0.1f);
        GUI.Box(new Rect(10, 68, 480, 26), "  " + msg, style);
    }
}
