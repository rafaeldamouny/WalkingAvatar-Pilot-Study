using System.Collections.Generic;
using UnityEngine;

namespace PerceptionPilot
{
    /// <summary>
    /// Plays real biological motion: one dot per recorded joint, at the positions the motion-capture system
    /// measured on the real walker (FreeMoCap). Same placement/cutoff logic as the avatar: positions are
    /// expressed relative to the hip midpoint at the start of the shown segment, rotated and translated by
    /// the trial manager, and hidden one frame after the cutoff.
    /// </summary>
    public class RealPointLightPlayer : MonoBehaviour
    {
        [Header("Data interpretation (FreeMoCap 3D output: Z up, millimetres)")]
        public CaptureUpAxis upAxis = CaptureUpAxis.ZUp;
        [Tooltip("Metres per data unit. 0 = auto (mm detected from leg length).")]
        public float unitScaleOverride = 0f;
        [Tooltip("Add the two foot markers (15 instead of the classic 13).")]
        public bool includeFeet = false;

        [Header("Appearance")]
        [Tooltip("Unlit material (the setup menu creates PP_Dots.mat).")]
        public Material dotMaterial;
        public float dotDiameter = 0.05f;

        readonly List<Transform> _dots = new List<Transform>();
        PointLightTrajectory _traj;
        Vector3 _startPos, _rootStart;
        Quaternion _rot = Quaternion.identity;
        float _t, _tEnd;
        bool _playing, _hidePending;

        public bool IsPlaying => _playing;
        public Vector3 LastShownPosition { get; private set; }
        /// <summary>Follows the hip midpoint so loggers can use it as "avatar position".</summary>
        public Transform RootProxy { get; private set; }

        void Awake()
        {
            RootProxy = new GameObject("PLD_RootProxy").transform;
            RootProxy.SetParent(transform, false);
        }

        public PointLightTrajectory Load(ManeuverEntry e)
        {
            if (e == null || e.pointLightCsv == null) return null;
            try { return PointLightTrajectory.Get(e.pointLightCsv, e.pointLightFps, upAxis, unitScaleOverride, includeFeet); }
            catch (System.Exception ex) { Debug.LogError($"[RealPointLight] {e.pointLightCsv.name}: {ex.Message}"); return null; }
        }

        void EnsureDots(int n)
        {
            if (_dots.Count == n) return;
            foreach (var d in _dots) if (d) Destroy(d.gameObject);
            _dots.Clear();

            Material mat = dotMaterial;
            if (mat == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                mat = new Material(sh);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            }
            for (int i = 0; i < n; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "RealPLD_" + i;
                var col = go.GetComponent<Collider>(); if (col) Destroy(col);
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * dotDiameter;
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                go.SetActive(false);
                _dots.Add(go.transform);
            }
        }

        public void SetVisible(bool v)
        {
            foreach (var d in _dots) if (d) d.gameObject.SetActive(v);
        }

        /// <summary>Hip-midpoint positions relative to the start of the shown segment (horizontal frame of the data).</summary>
        public AvatarStimulus.SimResult Simulate(PointLightTrajectory traj, double tStart, double tBaselineEnd, double tT0, double tPlacement)
        {
            var r0 = traj.Root((float)tStart);
            Vector3 Rel(double t) { var p = traj.Root((float)t) - r0; return new Vector3(p.x, 0f, p.z); }
            return new AvatarStimulus.SimResult
            {
                posAtBaselineEnd = Rel(tBaselineEnd),
                posAtT0 = Rel(tT0),
                posAtPlacement = Rel(tPlacement),
                ok = true
            };
        }

        public void BeginPlayback(PointLightTrajectory traj, Vector3 startPos, Quaternion rot, double tStart, double tEnd)
        {
            _traj = traj;
            EnsureDots(traj.MarkerCount);
            _startPos = startPos; _rot = rot;
            _rootStart = traj.Root((float)tStart);
            _t = (float)tStart;
            _tEnd = Mathf.Min((float)tEnd, traj.Duration);
            _hidePending = false;
            _playing = true;
            Apply();
            SetVisible(true);
        }

        public void StopAndHide()
        {
            _playing = false; _hidePending = false;
            SetVisible(false);
        }

        Vector3 World(Vector3 p)
        {
            var h = p - _rootStart; h.y = 0f;
            var w = _startPos + _rot * h;
            w.y = _startPos.y + p.y;  // recorded height above the floor
            return w;
        }

        void Apply()
        {
            for (int m = 0; m < _dots.Count; m++) _dots[m].position = World(_traj.Marker(m, _t));
            var root = World(_traj.Root(_t));
            LastShownPosition = root;
            if (RootProxy) RootProxy.position = root;
        }

        void Update()
        {
            if (!_playing) return;
            if (_hidePending) { StopAndHide(); return; }
            _t = Mathf.Min(_t + Time.deltaTime, _tEnd);
            Apply();
            if (_t >= _tEnd - 1e-6f) _hidePending = true; // cutoff frame is rendered once, then hidden
        }
    }
}
