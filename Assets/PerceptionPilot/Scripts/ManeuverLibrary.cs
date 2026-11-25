using System.Collections.Generic;
using UnityEngine;

namespace PerceptionPilot
{
    /// <summary>All recorded takes available to the experiment.
    /// Build it with Tools > Perception Pilot > 2. Build / update library from selection.</summary>
    [CreateAssetMenu(menuName = "Perception Pilot/Maneuver Library", fileName = "ManeuverLibrary")]
    public class ManeuverLibrary : ScriptableObject
    {
        public List<ManeuverEntry> entries = new List<ManeuverEntry>();

        public List<ManeuverEntry> UsableOf(ManeuverType type)
        {
            var list = new List<ManeuverEntry>();
            foreach (var e in entries)
                if (e != null && e.type == type && e.IsUsable) list.Add(e);
            return list;
        }
    }
}
