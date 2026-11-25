using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class AutoWalkDriver : MonoBehaviour
{
    // ── Movement ─────────────────────────────────────────────────
    [Header("Movement")]
    public float MoveSpeed       = 1.0f;
    public float SpeedChangeRate = 5.0f;
    public float TurnSpeed       = 60f;

    [Header("Gravity")]
    public float Gravity     = -15f;
    public float FallTimeout = 0.15f;

    [Header("Ground Check")]
    public float     GroundedOffset = -0.14f;
    public float     GroundedRadius = 0.28f;
    public LayerMask GroundLayers;

    [Header("Goal / Waypoints")]
    public float GoalRadius    = 0.50f;
    public float WpRadius      = 0.18f;
    public float LookaheadDist = 0.70f;

    // ── Hybrid Planner ────────────────────────────────────────────
    [Header("Safety Margins (obstacles expanded by CC radius + both margins)")]
    public float AvatarMargin      = 0.15f;   // avatar body clearance
    public float ParticipantMargin = 0.20f;   // extra room for participant beside avatar

    [Header("Path Cost Weights")]
    [Tooltip("Weight on raw edge length.")]
    public float WeightDist                = 1.0f;
    [Tooltip("Weight on integrated lateral² — stay near start→goal axis. Accumulates with distance.")]
    public float WeightCenter              = 0.50f;
    [Tooltip("Weight on short-range obstacle proximity (only within ExtraComfortDistance).")]
    public float WeightObstacleProximity   = 3.0f;
    [Tooltip("Turn penalty weight (logged; used when K-paths are added).")]
    public float WeightTurn                = 0.4f;

    [Header("Path Planning")]
    [Tooltip("Soft clearance buffer beyond mandatory expansion. Penalty fades quadratically to zero at this distance.")]
    public float ExtraComfortDistance = 0.30f;
    [Tooltip("Centerline anchor spacing (m) along the start→goal axis. Gives Dijkstra states to return toward center.")]
    public float AnchorSpacing        = 1.0f;
    [Tooltip("Sample step for edge cost integration.")]
    public float EdgeSampleStep       = 0.20f;
    [Tooltip("Catmull-Rom sample distance (m). Smaller = smoother.")]
    public float SplineStep           = 0.10f;
    [Tooltip("Max gap between Catmull-Rom control points.")]
    public float MaxControlDist       = 1.20f;

    [Header("Animation")]
    public float AnimWalkSpeed     = 1f;
    public float TurnAngleFullLean = 35f;
    public float TurnBlendRate     = 4.0f;

    [Header("Path Visualisation")]
    public float LineWidth   = 0.05f;
    public float SampleDist  = 0.08f;
    public int   TraceMaxPts = 600;
    public Color WalkedColor  = new Color(0.15f, 1f,    0.3f,  0.92f);
    public Color PlannedColor = new Color(1f,    0.85f, 0.1f,  0.95f);
    public Color GoalColor    = new Color(0.4f,  0.95f, 1f,    0.80f);

    // ── Anim hashes ───────────────────────────────────────────────
    static readonly int SpeedHash     = Animator.StringToHash("Speed");
    static readonly int TurnAngleHash = Animator.StringToHash("TurnAngle");
    static readonly int GroundedHash  = Animator.StringToHash("Grounded");
    static readonly int FreeFallHash  = Animator.StringToHash("FreeFall");

    // ── Components ────────────────────────────────────────────────
    CharacterController _cc;
    Animator            _anim;
    bool                _hasAnim;

    // ── Locomotion ────────────────────────────────────────────────
    float _speed, _turnAngle, _vertVel, _fallTimer;
    bool  _grounded = true;
    const float TermVel = 53f;

    // ── Path ──────────────────────────────────────────────────────
    List<Vector3> _waypoints    = new List<Vector3>();
    int           _wpIndex;
    Vector3       _goalPos;
    Vector3       _previewStart = Vector3.positiveInfinity;
    bool          _walking;

    // ── Stuck recovery ────────────────────────────────────────────
    float _stuckTimer;

    // ── Visualisation ─────────────────────────────────────────────
    LineRenderer           _lrWalked, _lrPlanned, _lrGoal;
    readonly List<Vector3> _ptWalked = new List<Vector3>();
    Vector3                _lastSample;

    // ─────────────────────────────────────────────────────────────
    //  2-D axis-aligned box in XZ plane
    // ─────────────────────────────────────────────────────────────
    struct Box2
    {
        public float x0, x1, z0, z1;
        public bool Inside(float x, float z) => x > x0 && x < x1 && z > z0 && z < z1;
    }

    // ─────────────────────────────────────────────────────────────
    void Awake()
    {
        _cc            = GetComponent<CharacterController>();
        _cc.height     = 1.8f;
        _cc.radius     = 0.28f;
        _cc.center     = new Vector3(0f, 0.93f, 0f);
        _cc.skinWidth  = 0.02f;
        _cc.stepOffset = 0.3f;
        _cc.slopeLimit = 45f;

        _hasAnim = TryGetComponent(out _anim);
        if (!_hasAnim) _hasAnim = (_anim = GetComponentInChildren<Animator>()) != null;
        if (_hasAnim) _anim.applyRootMotion = false;

        if (GroundLayers.value == 0) GroundLayers = ~0;
        _fallTimer = FallTimeout;

        _lrWalked  = MakeLR("TracWalked",  WalkedColor,  LineWidth);
        _lrPlanned = MakeLR("TracPlanned", PlannedColor, LineWidth * 1.5f);
        _lrGoal    = MakeLR("GoalLine",    GoalColor,    LineWidth * 0.6f);
    }

    void Update()
    {
        if (!_walking) return;
        GroundCheck();
        GravityStep();
        try   { MoveStep(); }
        catch (System.Exception e)
        { Debug.LogError("[AutoWalkDriver] " + e.Message); StopWalk(); }
        RecordTrace();
    }

    // ── Ground / Gravity ──────────────────────────────────────────
    void GroundCheck()
    {
        var sp = new Vector3(transform.position.x,
                             transform.position.y - GroundedOffset,
                             transform.position.z);
        _grounded = Physics.CheckSphere(sp, GroundedRadius, GroundLayers,
                                        QueryTriggerInteraction.Ignore);
        if (_hasAnim) _anim.SetBool(GroundedHash, _grounded);
    }

    void GravityStep()
    {
        if (_grounded)
        {
            _fallTimer = FallTimeout;
            if (_hasAnim) _anim.SetBool(FreeFallHash, false);
            if (_vertVel < 0f) _vertVel = -2f;
        }
        else
        {
            if (_fallTimer >= 0f) _fallTimer -= Time.deltaTime;
            else if (_hasAnim)   _anim.SetBool(FreeFallHash, true);
        }
        if (_vertVel < TermVel) _vertVel += Gravity * Time.deltaTime;
    }

    // ── Waypoint follower ─────────────────────────────────────────
    void MoveStep()
    {
        if (_wpIndex >= _waypoints.Count) { StopWalk(); return; }

        Vector3 toWp = Flat(_waypoints[_wpIndex] - transform.position);
        while (toWp.magnitude < WpRadius && _wpIndex < _waypoints.Count - 1)
        {
            _wpIndex++;
            _stuckTimer = 0f;
            toWp = Flat(_waypoints[_wpIndex] - transform.position);
        }

        if (_wpIndex == _waypoints.Count - 1 && toWp.magnitude < GoalRadius)
        { StopWalk(); Debug.Log("[AutoWalkDriver] Goal reached ✅"); return; }

        Vector3 steer  = Flat(GetLookahead(LookaheadDist) - transform.position);
        float   targetY = steer.sqrMagnitude > 0.01f
            ? Vector3.SignedAngle(Vector3.forward, steer.normalized, Vector3.up)
            : transform.eulerAngles.y;

        transform.rotation = Quaternion.Euler(0f,
            Mathf.MoveTowardsAngle(transform.eulerAngles.y, targetY,
                                   TurnSpeed * Time.deltaTime), 0f);

        float curH = Flat(_cc.velocity).magnitude;
        _speed = Mathf.Lerp(curH, MoveSpeed, Time.deltaTime * SpeedChangeRate);

        float headErr  = Mathf.DeltaAngle(transform.eulerAngles.y, targetY);
        _turnAngle = Mathf.Lerp(_turnAngle,
            Mathf.Clamp(headErr / TurnAngleFullLean, -1f, 1f),
            Time.deltaTime * TurnBlendRate);

        _cc.Move(transform.forward * _speed   * Time.deltaTime
               + Vector3.up        * _vertVel * Time.deltaTime);

        if (curH < 0.08f)
        {
            _stuckTimer += Time.deltaTime;
            if (_stuckTimer > 1.2f)
            {
                _stuckTimer = 0f;
                _wpIndex = Mathf.Min(_wpIndex + 5, _waypoints.Count - 1);
                Debug.LogWarning($"[AutoWalkDriver] Stuck — skipping to wp {_wpIndex}.");
            }
        }
        else _stuckTimer = 0f;

        if (_hasAnim)
        {
            _anim.speed = _speed > 0.05f ? 1f : 0f;
            _anim.SetFloat(SpeedHash,     _speed > 0.05f ? AnimWalkSpeed : 0f);
            _anim.SetFloat(TurnAngleHash, _turnAngle);
        }
    }

    Vector3 GetLookahead(float dist)
    {
        float   rem  = dist;
        Vector3 from = transform.position;
        for (int i = _wpIndex; i < _waypoints.Count; i++)
        {
            float seg = Flat(_waypoints[i] - from).magnitude;
            if (seg >= rem)
            {
                Vector3 d = Flat(_waypoints[i] - from).normalized;
                return new Vector3(from.x + d.x * rem, _waypoints[i].y, from.z + d.z * rem);
            }
            rem -= seg; from = _waypoints[i];
        }
        return _waypoints[_waypoints.Count - 1];
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    void RecordTrace()
    {
        Vector3 p = transform.position + Vector3.up * 0.04f;
        if (Vector3.Distance(p, _lastSample) < SampleDist) return;
        _lastSample = p;
        _ptWalked.Add(p);
        if (_ptWalked.Count > TraceMaxPts) _ptWalked.RemoveAt(0);
        _lrWalked.positionCount = _ptWalked.Count;
        _lrWalked.SetPositions(_ptWalked.ToArray());
    }

    // ═══════════════════════════════════════════════════════════════
    //  HYBRID PLANNER
    //
    //  Graph vertices: Start + Goal + expanded-box corners + centerline anchors
    //
    //  Edge cost  C(e) = WeightDist * L
    //                  + WeightCenter * ∫ lateral²  ds      (integrated, not averaged)
    //                  + WeightObstacleProximity * ∫ prox² ds   (finite range only)
    //
    //  Obstacle expansion enforces hard safety.
    //  Integrated center cost means "stay off-axis for longer = proportionally more expensive."
    //  Finite-range proximity only penalizes scraping the safety boundary — not being far away.
    //  Centerline anchors give Dijkstra graph states to return toward center after a detour.
    // ═══════════════════════════════════════════════════════════════

    // ── Obstacle expansion ────────────────────────────────────────
    List<Box2> BuildBoxes()
    {
        float r   = _cc.radius * Mathf.Abs(transform.lossyScale.x);
        float exp = r + AvatarMargin + ParticipantMargin;
        var   result = new List<Box2>();

        foreach (var go in ObstaclePlacer.Placed)
        {
            if (go == null) continue;
            var col = go.GetComponent<Collider>();
            if (col == null) continue;
            var b = col.bounds;
            result.Add(new Box2 {
                x0 = b.min.x - exp, x1 = b.max.x + exp,
                z0 = b.min.z - exp, z1 = b.max.z + exp });
        }

        var wp = FindObjectOfType<WallPlacer>();
        if (wp != null)
            foreach (var w in wp.Walls)
            {
                if (w == null) continue;
                var col = w.GetComponent<Collider>();
                if (col == null) continue;
                var b = col.bounds;
                result.Add(new Box2 {
                    x0 = b.min.x - exp, x1 = b.max.x + exp,
                    z0 = b.min.z - exp, z1 = b.max.z + exp });
            }

        return result;
    }

    // ── 2-D segment vs AABB intersection ─────────────────────────
    static bool SegBlocked(Vector2 a, Vector2 b, List<Box2> boxes)
    {
        foreach (var bx in boxes)
            if (SegHitsBox(a, b, bx)) return true;
        return false;
    }

    static bool SegHitsBox(Vector2 a, Vector2 b, Box2 bx)
    {
        if (bx.Inside(a.x, a.y) || bx.Inside(b.x, b.y)) return true;
        Vector2 tl = new Vector2(bx.x0, bx.z0), tr = new Vector2(bx.x1, bx.z0);
        Vector2 bl = new Vector2(bx.x0, bx.z1), br = new Vector2(bx.x1, bx.z1);
        return SegsCross(a, b, tl, tr) || SegsCross(a, b, tr, br) ||
               SegsCross(a, b, br, bl) || SegsCross(a, b, bl, tl);
    }

    static bool SegsCross(Vector2 p, Vector2 q, Vector2 r, Vector2 s)
    {
        Vector2 d = q - p, e = s - r, f = r - p;
        float cross = d.x * e.y - d.y * e.x;
        if (Mathf.Abs(cross) < 1e-9f) return false;
        float t = (f.x * e.y - f.y * e.x) / cross;
        float u = (f.x * d.y - f.y * d.x) / cross;
        return t > 1e-6f && t < 1f - 1e-6f &&
               u > 1e-6f && u < 1f - 1e-6f;
    }

    // ── Nearest expanded-box surface distance ─────────────────────
    // Returns distance from p to the nearest box surface.
    // If p is inside a box, returns the escape distance to nearest face (still > 0).
    static float NearestBoxSurfaceDist(Vector2 p, List<Box2> boxes)
    {
        float minDist = float.MaxValue;
        foreach (var bx in boxes)
        {
            float d;
            if (bx.Inside(p.x, p.y))
            {
                // Distance to nearest face from inside
                d = Mathf.Min(
                    Mathf.Min(p.x - bx.x0, bx.x1 - p.x),
                    Mathf.Min(p.y - bx.z0, bx.z1 - p.y));
            }
            else
            {
                float dx = Mathf.Max(0f, Mathf.Max(bx.x0 - p.x, p.x - bx.x1));
                float dz = Mathf.Max(0f, Mathf.Max(bx.z0 - p.y, p.y - bx.z1));
                d = Mathf.Sqrt(dx * dx + dz * dz);
            }
            if (d < minDist) minDist = d;
        }
        return minDist == float.MaxValue ? 999f : minDist;
    }

    // ── Weighted edge cost ────────────────────────────────────────
    // s2   = 2D start of the full path (lateral deviation reference)
    // perp = unit vector perpendicular to start→goal axis
    float ComputeEdgeCost(Vector2 a, Vector2 b, Vector2 s2, Vector2 perp, List<Box2> boxes)
    {
        float dist = Vector2.Distance(a, b);
        if (dist < 1e-4f) return 0f;

        int   steps        = Mathf.Max(1, Mathf.CeilToInt(dist / EdgeSampleStep));
        float sStep        = dist / steps;   // actual step size — used as ds for integration
        float centerCost   = 0f;
        float proxCost     = 0f;

        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, i / (float)steps);

            // ── Center deviation: integrated lateral² ds
            //    A 20 m detour costs ~10× more than a 2 m detour at the same offset.
            float lateral  = Vector2.Dot(p - s2, perp);
            centerCost    += lateral * lateral * sStep;

            // ── Obstacle proximity: finite-range quadratic, fades to 0 at ExtraComfortDistance.
            //    No penalty beyond that distance — prevents fleeing far from all obstacles.
            float clearance = NearestBoxSurfaceDist(p, boxes);
            if (clearance < ExtraComfortDistance)
            {
                float t  = (ExtraComfortDistance - clearance) / ExtraComfortDistance;
                proxCost += t * t * sStep;
            }
        }

        return WeightDist              * dist
             + WeightCenter            * centerCost
             + WeightObstacleProximity * proxCost;
    }

    // ── Turn penalty — on selected path, logged only ──────────────
    float ComputeTurnPenalty(List<Vector3> path)
    {
        if (path.Count < 3) return 0f;
        float penalty = 0f;
        for (int i = 1; i < path.Count - 1; i++)
        {
            float angle = Vector3.Angle(
                (path[i]     - path[i - 1]).normalized,
                (path[i + 1] - path[i    ]).normalized);
            penalty += angle * angle;
        }
        return WeightTurn * penalty;
    }

    // ── Main path computation ─────────────────────────────────────
    List<Vector3> ComputePath(Vector3 start, Vector3 goal)
    {
        var   boxes = BuildBoxes();
        float y     = start.y;

        Vector2 s2 = new Vector2(start.x, start.z);
        Vector2 g2 = new Vector2(goal.x,  goal.z);

        // Corridor axis + perpendicular for center deviation
        Vector2 rawAxis = g2 - s2;
        Vector2 axis    = rawAxis.sqrMagnitude > 1e-6f ? rawAxis.normalized : Vector2.right;
        Vector2 perp    = new Vector2(-axis.y, axis.x);

        // ── Fast path: direct line of sight
        if (!SegBlocked(s2, g2, boxes))
        {
            Debug.Log("[AutoWalkDriver] Direct path — no obstacles.");
            return BuildSpline(new List<Vector3> { start, goal }, boxes, y);
        }

        // ── Vertex set ────────────────────────────────────────────
        var verts = new List<Vector3>();
        verts.Add(start);   // index 0
        verts.Add(goal);    // index 1

        // Expanded obstacle corners (only if not inside any other box)
        foreach (var bx in boxes)
        {
            Vector3[] corners =
            {
                new Vector3(bx.x0, y, bx.z0),
                new Vector3(bx.x1, y, bx.z0),
                new Vector3(bx.x0, y, bx.z1),
                new Vector3(bx.x1, y, bx.z1),
            };
            foreach (var c in corners)
            {
                bool blocked = false;
                foreach (var other in boxes)
                    if (other.Inside(c.x, c.z)) { blocked = true; break; }
                if (!blocked) verts.Add(c);
            }
        }

        // Centerline anchor points every AnchorSpacing meters along start→goal axis.
        // These give Dijkstra explicit graph states to return toward center after a detour.
        float totalDist  = rawAxis.magnitude;
        int   numAnchors = Mathf.FloorToInt(totalDist / AnchorSpacing);
        for (int k = 1; k < numAnchors; k++)
        {
            float   t        = k / (float)numAnchors;
            Vector2 anchorXZ = Vector2.Lerp(s2, g2, t);
            // Only add if this anchor is not inside any expanded box
            bool blocked = false;
            foreach (var bx in boxes)
                if (bx.Inside(anchorXZ.x, anchorXZ.y)) { blocked = true; break; }
            if (!blocked)
                verts.Add(new Vector3(anchorXZ.x, y, anchorXZ.y));
        }

        int n = verts.Count;
        Debug.Log($"[AutoWalkDriver] Graph: {n} vertices ({numAnchors - 1} anchors), {boxes.Count} boxes.");

        // ── Weighted edge cost matrix ─────────────────────────────
        float[] edgeCost = new float[n * n];
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            Vector2 va = new Vector2(verts[i].x, verts[i].z);
            Vector2 vb = new Vector2(verts[j].x, verts[j].z);
            if (!SegBlocked(va, vb, boxes))
            {
                float cost = ComputeEdgeCost(va, vb, s2, perp, boxes);
                edgeCost[i * n + j] = edgeCost[j * n + i] = cost;
            }
        }

        // ── Dijkstra from vertex 0 (start) to vertex 1 (goal) ────
        float[] distArr = new float[n];
        int[]   prev    = new int[n];
        bool[]  visited = new bool[n];
        for (int i = 0; i < n; i++) { distArr[i] = float.MaxValue; prev[i] = -1; }
        distArr[0] = 0f;

        for (int iter = 0; iter < n; iter++)
        {
            int u = -1;
            for (int i = 0; i < n; i++)
                if (!visited[i] && (u < 0 || distArr[i] < distArr[u])) u = i;
            if (u < 0 || distArr[u] == float.MaxValue) break;
            if (u == 1) break;
            visited[u] = true;

            for (int v = 0; v < n; v++)
            {
                float e = edgeCost[u * n + v];
                if (e <= 0f || visited[v]) continue;
                float nd = distArr[u] + e;
                if (nd < distArr[v]) { distArr[v] = nd; prev[v] = u; }
            }
        }

        // ── Reconstruct path ─────────────────────────────────────
        if (prev[1] < 0)
        {
            Debug.LogWarning("[AutoWalkDriver] No path found — falling back to direct line.");
            return BuildSpline(new List<Vector3> { start, goal }, boxes, y);
        }

        var path = new List<Vector3>();
        int cur = 1;
        while (cur >= 0) { path.Add(verts[cur]); cur = prev[cur]; }
        path.Reverse();

        float tp = ComputeTurnPenalty(path);
        Debug.Log($"[AutoWalkDriver] Path: {path.Count} corners, turn penalty={tp:F2}");

        return BuildSpline(path, boxes, y);
    }

    // ── Spline pipeline with safety validation ─────────────────────
    List<Vector3> BuildSpline(List<Vector3> controlPts, List<Box2> boxes, float y)
    {
        if (controlPts.Count < 2) return controlPts;
        var spline = CatmullRom(InsertMidpoints(controlPts, MaxControlDist), SplineStep);
        ValidateSpline(spline, boxes, y);
        return spline;
    }

    // Push any spline point inside an expanded box to its nearest face.
    // Handles the rare case where a tight Catmull-Rom curve bows through a corner.
    void ValidateSpline(List<Vector3> spline, List<Box2> boxes, float y)
    {
        int pushed = 0;
        for (int i = 0; i < spline.Count; i++)
        {
            Vector2 p = new Vector2(spline[i].x, spline[i].z);
            foreach (var bx in boxes)
            {
                if (bx.Inside(p.x, p.y))
                {
                    p = PushOutOfBox(p, bx);
                    pushed++;
                    break;
                }
            }
            spline[i] = new Vector3(p.x, y, p.y);
        }
        if (pushed > 0)
            Debug.Log($"[AutoWalkDriver] ValidateSpline: corrected {pushed} points.");
    }

    static Vector2 PushOutOfBox(Vector2 p, Box2 bx)
    {
        float dLeft   = p.x - bx.x0;
        float dRight  = bx.x1 - p.x;
        float dBottom = p.y - bx.z0;
        float dTop    = bx.z1 - p.y;
        float minD    = Mathf.Min(dLeft, Mathf.Min(dRight, Mathf.Min(dBottom, dTop)));
        const float nudge = 0.01f;
        if (Mathf.Approximately(minD, dLeft))   return new Vector2(bx.x0 - nudge, p.y);
        if (Mathf.Approximately(minD, dRight))  return new Vector2(bx.x1 + nudge, p.y);
        if (Mathf.Approximately(minD, dBottom)) return new Vector2(p.x, bx.z0 - nudge);
                                                return new Vector2(p.x, bx.z1 + nudge);
    }

    // ── Catmull-Rom spline ────────────────────────────────────────
    static List<Vector3> InsertMidpoints(List<Vector3> pts, float maxDist)
    {
        var r = new List<Vector3> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            float d = Vector3.Distance(pts[i - 1], pts[i]);
            int   n = Mathf.CeilToInt(d / maxDist);
            for (int j = 1; j <= n; j++)
                r.Add(Vector3.Lerp(pts[i - 1], pts[i], j / (float)n));
        }
        return r;
    }

    static List<Vector3> CatmullRom(List<Vector3> pts, float step)
    {
        if (pts.Count < 2) return new List<Vector3>(pts);
        var ext = new List<Vector3>();
        ext.Add(pts[0] + (pts[0] - pts[1]));
        ext.AddRange(pts);
        ext.Add(pts[pts.Count - 1] + (pts[pts.Count - 1] - pts[pts.Count - 2]));
        var result = new List<Vector3>();
        for (int i = 1; i < ext.Count - 2; i++)
        {
            Vector3 p0 = ext[i - 1], p1 = ext[i], p2 = ext[i + 1], p3 = ext[i + 2];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(p1, p2) / step));
            for (int s = 0; s < steps; s++)
            {
                float t = s / (float)steps, t2 = t * t, t3 = t2 * t;
                result.Add(0.5f * (
                    2f * p1 +
                    (-p0 + p2)                          * t  +
                    (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                    (-p0 + 3f * p1 - 3f * p2 + p3)     * t3));
            }
        }
        result.Add(pts[pts.Count - 1]);
        return result;
    }

    // ── Visualisation ─────────────────────────────────────────────
    void ShowPath(List<Vector3> pts)
    {
        if (_lrPlanned == null || pts == null) return;
        _lrPlanned.positionCount = pts.Count;
        var arr = new Vector3[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            arr[i] = new Vector3(pts[i].x, pts[i].y + 0.03f, pts[i].z);
        _lrPlanned.SetPositions(arr);
    }

    void ShowGoalLine(Vector3 from, Vector3 to)
    {
        if (_lrGoal == null) return;
        _lrGoal.positionCount = 2;
        _lrGoal.SetPosition(0, from + Vector3.up * 0.03f);
        _lrGoal.SetPosition(1, to   + Vector3.up * 0.03f);
    }

    // ── Public API ────────────────────────────────────────────────
    public void PreviewPath(Vector3 goalPosition)
    {
        _previewStart = transform.position;
        _goalPos      = goalPosition;
        _waypoints    = ComputePath(transform.position, goalPosition);
        _wpIndex      = 0;
        ShowPath(_waypoints);
        ShowGoalLine(transform.position, goalPosition);
        Debug.Log($"[AutoWalkDriver] Path preview: {_waypoints.Count} spline pts.");
    }

    public void StartWalk(Vector3 goalPosition)
    {
        _goalPos = goalPosition;

        bool reusePreview = _waypoints != null && _waypoints.Count > 1 &&
                            Vector3.Distance(transform.position, _previewStart) < 0.5f;
        if (!reusePreview)
        {
            _waypoints = ComputePath(transform.position, goalPosition);
            ShowPath(_waypoints);
        }
        ShowGoalLine(transform.position, goalPosition);

        _wpIndex    = 0;
        _walking    = true;
        _stuckTimer = 0f;
        _lastSample = transform.position;
        _speed      = 0f;
        _turnAngle  = 0f;
        _ptWalked.Clear();
        if (_lrWalked != null) _lrWalked.positionCount = 0;

        if (_hasAnim) _anim.speed = 1f;
        Debug.Log($"[AutoWalkDriver] StartWalk → {_waypoints.Count} pts, goal={goalPosition}.");
    }

    public void StopWalk()
    {
        _walking   = false;
        _speed     = 0f;
        _turnAngle = 0f;
        if (_hasAnim)
        {
            _anim.speed = 0f;
            _anim.SetFloat(SpeedHash, 0f);
            _anim.SetFloat(TurnAngleHash, 0f);
            _anim.SetBool(GroundedHash, true);
            _anim.SetBool(FreeFallHash, false);
        }
    }

    public void Cleanup()
    {
        if (_lrWalked  != null) { Destroy(_lrWalked.gameObject);  _lrWalked  = null; }
        if (_lrPlanned != null) { Destroy(_lrPlanned.gameObject); _lrPlanned = null; }
        if (_lrGoal    != null) { Destroy(_lrGoal.gameObject);    _lrGoal    = null; }
    }

    // ── LineRenderer helpers ──────────────────────────────────────
    LineRenderer MakeLR(string name, Color col, float w)
    {
        var go = new GameObject(name);
        go.transform.SetParent(null);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true; lr.positionCount = 0;
        lr.startWidth = lr.endWidth = w;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.material = BuildMat(col);
        return lr;
    }

    Material BuildMat(Color col)
    {
        var s = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        var m = new Material(s);
        m.color = col;
        m.SetFloat("_Surface", 1);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.renderQueue = 4000;
        return m;
    }

    // ── Debug HUD ─────────────────────────────────────────────────
    void OnGUI()
    {
        int total = _waypoints != null ? _waypoints.Count : 0;
        string msg = _walking
            ? $"WALKING  wp {_wpIndex + 1}/{total}  spd:{_speed:F2}  stuck:{_stuckTimer:F1}s"
            : $"STOPPED  ({total} spline pts planned)";
        Color col = _walking ? new Color(0.1f, 1f, 0.3f) : new Color(1f, 0.65f, 0.1f);
        var style = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleLeft };
        style.normal.textColor = col;
        GUI.Box(new Rect(10f, 68f, 480f, 26f), "  " + msg, style);
    }
}
