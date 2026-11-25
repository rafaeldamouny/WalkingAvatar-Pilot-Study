using UnityEngine;

namespace WalkingEyeExperiment
{
    // Adaptive low-pass on head-local directions: reduce fixation jitter and
    // increase responsiveness during large eye movements, without changing gain.
    public sealed class GazeWindowSmoother
    {
        bool initialized;
        Vector3 previousInput, filtered;
        Vector3 filteredVelocity;

        public void Reset() { initialized = false; filteredVelocity = Vector3.zero; }

        public Vector3 Step(Vector3 direction, float seconds, float minimumHz, float speedCoefficient, float speedFilterHz)
        {
            direction = direction.normalized;
            if (!initialized || seconds > .25f)
            {
                initialized = true;
                previousInput = filtered = direction;
                filteredVelocity = Vector3.zero;
                return filtered;
            }
            if (seconds <= 0) return filtered;
            // Filter signed velocity before taking its magnitude: alternating noise
            // cancels instead of being interpreted as sustained fast eye movement.
            Vector3 velocity = (direction - previousInput) / seconds;
            filteredVelocity = Vector3.Lerp(filteredVelocity, velocity, Alpha(speedFilterHz, seconds));
            float cutoff = Mathf.Max(.01f, minimumHz) + Mathf.Max(0, speedCoefficient) * filteredVelocity.magnitude * Mathf.Rad2Deg;
            filtered = Vector3.Slerp(filtered, direction, Alpha(cutoff, seconds)).normalized;
            previousInput = direction;
            return filtered;
        }

        static float Alpha(float hz, float seconds)
        { return 1f / (1f + 1f / (2f * Mathf.PI * Mathf.Max(.01f, hz) * seconds)); }
    }
}
