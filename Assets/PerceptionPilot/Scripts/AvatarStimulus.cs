using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace PerceptionPilot
{
    /// <summary>
    /// Plays one recorded take on the avatar with REAL root motion (the recording moves the body,
    /// no code steers it). Playback is driven manually so it can (1) pre-simulate a trial invisibly to
    /// find where the avatar will be at the cutoff, and (2) stop exactly at the cutoff frame.
    /// Put this on the GameObject that has the avatar's Animator. The avatar must sit at the scene root
    /// (or under an unscaled, unrotated parent) and must not have other movement scripts active.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class AvatarStimulus : MonoBehaviour
    {
        [Tooltip("Body meshes shown in Realistic mode. Empty = all renderers under this object at startup.")]
        public Renderer[] bodyRenderers;
        public PointLightRig pointLightRig;
        [Tooltip("Humanoid foot IK while playing (reduces retargeting foot slide).")]
        public bool footIK = true;
        [Tooltip("Step size for invisible pre-simulation (s).")]
        public float simulationStep = 1f / 90f;

        public struct SimResult
        {
            public Vector3 posAtBaselineEnd;   // all positions relative to the start pose (origin, identity)
            public Vector3 posAtT0;
            public Vector3 posAtPlacement;
            public bool ok;
        }

        Animator _anim;
        PlayableGraph _graph;
        AnimationClipPlayable _clipPlayable;
        AnimationClip _graphClip;
        AppearanceMode _mode = AppearanceMode.Realistic;
        bool _driveRoot;

        bool _playing, _hidePending;
        double _tEnd;

        public bool IsPlaying => _playing;
        public Vector3 LastShownPosition { get; private set; }
        public double LastShownClipTime { get; private set; }
        public AppearanceMode Mode => _mode;

        void Awake()
        {
            _anim = GetComponent<Animator>();
            _anim.applyRootMotion = true;
            _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; // must animate while hidden
            _anim.runtimeAnimatorController = null;                // playback comes only from our graph

            if (bodyRenderers == null || bodyRenderers.Length == 0)
            {
                var list = new List<Renderer>(GetComponentsInChildren<Renderer>(true));
                bodyRenderers = list.ToArray();
            }
            if (pointLightRig == null) pointLightRig = GetComponent<PointLightRig>();
            if (pointLightRig != null) pointLightRig.Build(_anim); // after collecting body renderers

            SetVisible(false);
        }

        void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }

        // Apply the recording's root motion ourselves (identical in simulation and playback).
        void OnAnimatorMove()
        {
            if (!_driveRoot) return;
            transform.SetPositionAndRotation(_anim.rootPosition, _anim.rootRotation);
        }

        public void SetMode(AppearanceMode mode)
        {
            _mode = mode;
            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            bool body = visible && _mode == AppearanceMode.Realistic;
            bool dots = visible && _mode == AppearanceMode.PointLight;
            if (bodyRenderers != null)
                foreach (var r in bodyRenderers) if (r != null) r.enabled = body;
            if (pointLightRig != null) pointLightRig.SetVisible(dots);
        }

        void EnsureGraph(AnimationClip clip)
        {
            if (_graph.IsValid() && _graphClip == clip) return;
            if (_graph.IsValid()) _graph.Destroy();

            _graph = PlayableGraph.Create("PerceptionPilot_" + name);
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(_graph, "Anim", _anim);
            _clipPlayable = AnimationClipPlayable.Create(_graph, clip);
            _clipPlayable.SetApplyFootIK(footIK);
            output.SetSourcePlayable(_clipPlayable);
            _graph.Play();
            _graphClip = clip;
        }

        void Seek(double t)
        {
            // Setting twice clears the previous time, so no root-motion jump is produced.
            _clipPlayable.SetTime(t);
            _clipPlayable.SetTime(t);
            _graph.Evaluate(0f);
        }

        void EvaluateTo(double target, float maxStep)
        {
            int guard = 0;
            while (_clipPlayable.GetTime() < target - 1e-6 && guard++ < 100000)
            {
                float step = (float)Math.Min(maxStep, target - _clipPlayable.GetTime());
                _graph.Evaluate(step);
            }
        }

        /// <summary>Invisibly plays the clip from tStart, starting at the origin with identity rotation,
        /// and returns where the root is at the baseline end, at T0 and at the placement time.</summary>
        public SimResult Simulate(AnimationClip clip, double tStart, double tBaselineEnd, double tT0, double tPlacement)
        {
            var res = new SimResult();
            if (clip == null) return res;
            SetVisible(false);
            _playing = false;
            EnsureGraph(clip);

            _driveRoot = false;
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Seek(tStart);
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _driveRoot = true;

            var checkpoints = new List<(double t, int id)>
            {
                (tBaselineEnd, 0), (tT0, 1), (tPlacement, 2)
            };
            checkpoints.Sort((a, b) => a.t.CompareTo(b.t));

            foreach (var cp in checkpoints)
            {
                if (cp.t > tStart) EvaluateTo(cp.t, simulationStep);
                Vector3 p = transform.position;
                if (cp.id == 0) res.posAtBaselineEnd = p;
                else if (cp.id == 1) res.posAtT0 = p;
                else res.posAtPlacement = p;
            }
            _driveRoot = false;
            res.ok = true;
            return res;
        }

        /// <summary>Starts visible playback. The avatar is shown from tStart and hidden after the frame at tEnd.</summary>
        public void BeginPlayback(AnimationClip clip, Vector3 startPos, Quaternion startRot, double tStart, double tEnd)
        {
            EnsureGraph(clip);
            _driveRoot = false;
            transform.SetPositionAndRotation(startPos, startRot);
            Seek(tStart);
            transform.SetPositionAndRotation(startPos, startRot);
            _driveRoot = true;

            _tEnd = Math.Min(tEnd, clip.length);
            _hidePending = false;
            _playing = true;
            LastShownPosition = transform.position;
            LastShownClipTime = tStart;
            SetVisible(true);
        }

        public void StopAndHide()
        {
            _playing = false;
            _hidePending = false;
            _driveRoot = false;
            SetVisible(false);
        }

        void Update()
        {
            if (!_playing) return;

            if (_hidePending)
            {
                // The cutoff frame was rendered last frame; now remove the avatar.
                StopAndHide();
                return;
            }

            double now = _clipPlayable.GetTime();
            float step = (float)Math.Min(Time.deltaTime, _tEnd - now);
            if (step > 0f) _graph.Evaluate(step);

            LastShownPosition = transform.position;
            LastShownClipTime = _clipPlayable.GetTime();

            if (_clipPlayable.GetTime() >= _tEnd - 1e-6) _hidePending = true;
        }
    }
}
