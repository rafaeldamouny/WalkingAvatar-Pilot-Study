using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PerceptionPilot
{
    /// <summary>
    /// Response after each trial:
    ///   - Right trigger: point the controller ray at the floor where the avatar is heading.
    ///     Gives a continuous heading angle (left negative) and a category (Left / Straight / Right).
    ///   - A button = stairs UP, B button = stairs DOWN.
    /// Keyboard (editor testing): Left/Up/Right arrows = -60 / 0 / +60 deg, U = stairs up, D = stairs down.
    /// </summary>
    public class ResponseCollector : MonoBehaviour
    {
        [Tooltip("OVRCameraRig > TrackingSpace > RightHandAnchor (or RightControllerAnchor).")]
        public Transform rayOrigin;
        public LineRenderer line;
        public float maxRayLength = 15f;
        [Tooltip("|angle| below this counts as STRAIGHT.")]
        public float straightBandDeg = 15f;
        public bool allowKeyboard = true;

        public struct Result
        {
            public ResponseCategory category;
            public bool hasAngle;
            public float angleDeg;     // heading relative to the participant's forward, measured from the avatar's last position
            public Vector3 hitPoint;
            public double rtSeconds;
        }

        bool _active;
        Vector3 _origin, _forward;
        float _floorY;
        double _tStart;
        Action<Result> _callback;
        Vector3 _hit;
        bool _hasHit;

        public bool IsActive => _active;

        void Awake()
        {
            if (line != null) line.enabled = false;
        }

        /// <param name="avatarLastPosition">Where the avatar was when it disappeared (angles are measured from here).</param>
        /// <param name="participantForward">Straight-ahead direction of the participant (horizontal).</param>
        public void Begin(Vector3 avatarLastPosition, Vector3 participantForward, float floorY, Action<Result> callback)
        {
            _origin = avatarLastPosition; _origin.y = floorY;
            _forward = participantForward; _forward.y = 0f; _forward.Normalize();
            _floorY = floorY;
            _callback = callback;
            _tStart = Time.realtimeSinceStartupAsDouble;
            _active = true;
            if (line != null) line.enabled = true;
        }

        public void Cancel()
        {
            _active = false;
            if (line != null) line.enabled = false;
        }

        void Update()
        {
            if (!_active) return;
            UpdateRay();

            bool trigger = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            bool up = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
            bool down = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
            Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);

            if (up) { FinishCategory(ResponseCategory.StairsUp); return; }
            if (down) { FinishCategory(ResponseCategory.StairsDown); return; }
            if (rightStick.x < -.65f) { FinishAngle(-60f); return; }
            if (rightStick.x > .65f) { FinishAngle(60f); return; }
            if (rightStick.y > .65f) { FinishAngle(0f); return; }
            if (trigger && _hasHit) { FinishPoint(_hit); return; }

#if ENABLE_INPUT_SYSTEM
            if (allowKeyboard && Keyboard.current != null)
            {
                var kb = Keyboard.current;
                if (kb.leftArrowKey.wasPressedThisFrame) FinishAngle(-60f);
                else if (kb.upArrowKey.wasPressedThisFrame) FinishAngle(0f);
                else if (kb.rightArrowKey.wasPressedThisFrame) FinishAngle(60f);
                else if (kb.uKey.wasPressedThisFrame) FinishCategory(ResponseCategory.StairsUp);
                else if (kb.dKey.wasPressedThisFrame) FinishCategory(ResponseCategory.StairsDown);
            }
#endif
        }

        void UpdateRay()
        {
            _hasHit = false;
            if (rayOrigin == null) return;
            Vector3 p = rayOrigin.position, d = rayOrigin.forward;
            Vector3 end = p + d * maxRayLength;
            if (d.y < -1e-3f)
            {
                float t = (_floorY - p.y) / d.y;
                if (t > 0f && t < maxRayLength * 3f) { end = p + d * t; _hit = end; _hasHit = true; }
            }
            if (line != null)
            {
                line.positionCount = 2;
                line.SetPosition(0, p);
                line.SetPosition(1, end);
            }
        }

        float AngleOf(Vector3 hit)
        {
            Vector3 v = hit - _origin; v.y = 0f;
            if (v.sqrMagnitude < 1e-6f) return 0f;
            return Vector3.SignedAngle(_forward, v, Vector3.up); // + = right
        }

        ResponseCategory CategoryOf(float angle)
        {
            if (Mathf.Abs(angle) <= straightBandDeg) return ResponseCategory.Straight;
            return angle < 0f ? ResponseCategory.Left : ResponseCategory.Right;
        }

        void FinishPoint(Vector3 hit)
        {
            float a = AngleOf(hit);
            Finish(new Result { category = CategoryOf(a), hasAngle = true, angleDeg = a, hitPoint = hit });
        }

        void FinishAngle(float a)
        {
            Finish(new Result { category = CategoryOf(a), hasAngle = true, angleDeg = a, hitPoint = _origin });
        }

        void FinishCategory(ResponseCategory c)
        {
            Finish(new Result { category = c, hasAngle = false, angleDeg = float.NaN, hitPoint = _origin });
        }

        void Finish(Result r)
        {
            r.rtSeconds = Time.realtimeSinceStartupAsDouble - _tStart;
            _active = false;
            if (line != null) line.enabled = false;
            var cb = _callback; _callback = null;
            cb?.Invoke(r);
        }
    }
}
