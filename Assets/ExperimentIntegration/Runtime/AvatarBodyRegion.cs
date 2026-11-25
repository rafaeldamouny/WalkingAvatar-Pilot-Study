using UnityEngine;

namespace WalkingEyeExperiment
{
    public enum BodyRegion { Head, UpperTorsoShoulders, Pelvis, LeftArm, RightArm, LeftLeg, RightLeg, LeftFoot, RightFoot }
    public class AvatarBodyRegion : MonoBehaviour
    {
        public BodyRegion region;
        public Transform avatarRoot;
    }
}
