using System;
using UnityEngine;

namespace PerceptionPilot
{
    public enum ManeuverType
    {
        Straight,
        TurnLeft30, TurnLeft60, TurnLeft90,
        TurnRight30, TurnRight60, TurnRight90,
        StairsUp, StairsDown,
        Unknown
    }

    public enum ResponseCategory { None, Left, Straight, Right, StairsUp, StairsDown }

    public enum AppearanceMode { Realistic, PointLight }

    public enum FieldCondition { Full, SimulatedRP }

    public static class ManeuverInfo
    {
        /// <summary>The 9 maneuvers used in the main design.</summary>
        public static readonly ManeuverType[] Experimental =
        {
            ManeuverType.Straight,
            ManeuverType.TurnLeft30, ManeuverType.TurnLeft60, ManeuverType.TurnLeft90,
            ManeuverType.TurnRight30, ManeuverType.TurnRight60, ManeuverType.TurnRight90,
            ManeuverType.StairsUp, ManeuverType.StairsDown
        };

        /// <summary>Signed heading change in degrees. Left is negative, right is positive.</summary>
        public static float TrueAngle(ManeuverType t)
        {
            switch (t)
            {
                case ManeuverType.TurnLeft30: return -30f;
                case ManeuverType.TurnLeft60: return -60f;
                case ManeuverType.TurnLeft90: return -90f;
                case ManeuverType.TurnRight30: return 30f;
                case ManeuverType.TurnRight60: return 60f;
                case ManeuverType.TurnRight90: return 90f;
                default: return 0f;
            }
        }

        public static ResponseCategory Category(ManeuverType t)
        {
            switch (t)
            {
                case ManeuverType.Straight: return ResponseCategory.Straight;
                case ManeuverType.TurnLeft30:
                case ManeuverType.TurnLeft60:
                case ManeuverType.TurnLeft90: return ResponseCategory.Left;
                case ManeuverType.TurnRight30:
                case ManeuverType.TurnRight60:
                case ManeuverType.TurnRight90: return ResponseCategory.Right;
                case ManeuverType.StairsUp: return ResponseCategory.StairsUp;
                case ManeuverType.StairsDown: return ResponseCategory.StairsDown;
                default: return ResponseCategory.None;
            }
        }

        public static bool IsTurn(ManeuverType t) => Category(t) == ResponseCategory.Left || Category(t) == ResponseCategory.Right;
        public static bool IsStairs(ManeuverType t) => t == ManeuverType.StairsUp || t == ManeuverType.StairsDown;
    }

    /// <summary>One recorded take of one maneuver.</summary>
    [Serializable]
    public class ManeuverEntry
    {
        public AnimationClip clip;
        public ManeuverType type = ManeuverType.Unknown;
        public string walkerId = "W1";
        public int take = 1;

        [Tooltip("Trajectory onset T0 (seconds, clip time): first sustained change of the body's path " +
                 "(lateral for turns, vertical for stairs). For straight clips this is a matched pseudo-T0.")]
        public float t0 = -1f;
        public bool t0IsPseudo;

        [Tooltip("Tick after checking/adjusting T0 by hand so the analyser will not overwrite it.")]
        public bool t0Locked;

        [Tooltip("Heading change measured from the clip itself (deg, right positive). Use it to check the label.")]
        public float measuredTurnDeg;
        public float clipLength;

        [Tooltip("Untick to exclude this take from the experiment.")]
        public bool include = true;

        [Tooltip("Practice/catch trials only: distance (m) from the root position at T0 to where the stairs visual is placed.")]
        public float stairsForwardOffset = 0.35f;

        public string analysisNote;

        [Header("Real biological motion (same take)")]
        [Tooltip("FreeMoCap saved_data/csv/body_trajectories.csv of THIS take, renamed to the clip's name (e.g. W1_TurnL60_T03.csv). " +
                 "The point-light display plays these recorded joint positions directly.")]
        public TextAsset pointLightCsv;
        [Tooltip("Frame rate of the recording the CSV came from.")]
        public float pointLightFps = 30f;
        [Tooltip("T0 measured on the recorded hip-midpoint path of the CSV (s).")]
        public float t0PointLight = -1f;
        public bool t0PointLightLocked;

        public bool IsUsable => clip != null && include && type != ManeuverType.Unknown && t0 > 0f;
        public bool HasRealPointLight => pointLightCsv != null && t0PointLight > 0f;
    }
}
