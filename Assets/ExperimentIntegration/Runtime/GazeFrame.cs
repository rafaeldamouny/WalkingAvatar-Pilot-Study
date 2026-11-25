using UnityEngine;
using PupilLabs;

namespace WalkingEyeExperiment
{
    public struct GazeFrame
    {
        public long sequence;
        public GazeData raw;
        public double receivedRealtime;
        public bool hasSample, valid, simulated, calibrated;
        public string invalidReason, rayKind;
        public Ray ray, leftRay, rightRay;
        public bool dualAvailable, leftValid, rightValid;
        public Vector3 headPosition;
        public Quaternion headRotation;
        public string hitObject, bodyRegion;
        public Vector3 hitPoint;
        public bool hit;
    }
}
