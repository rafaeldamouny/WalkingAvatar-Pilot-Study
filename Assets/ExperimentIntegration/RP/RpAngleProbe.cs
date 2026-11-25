using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Verification aid: head-locked dots at known eccentricities (2.5, 5, 7.5, 10, 15, 20°) on 8 meridians, 2 m ahead,
    /// plus a centre fixation dot. In head-centred mode with a 15° field, rings up to 5° stay fully visible, the 7.5° ring
    /// sits at the 50% point of the soft edge, and 10°+ rings disappear. In gaze-centred mode, fixating a dot moves the
    /// field to it. Rings are coloured by eccentricity (centre white, 2.5° green, 5° cyan, 7.5° yellow, 10° orange,
    /// 15° red, 20° magenta).
    /// </summary>
    public sealed class RpAngleProbe : MonoBehaviour
    {
        static readonly float[] Rings = { 2.5f, 5f, 7.5f, 10f, 15f, 20f };
        static readonly Color[] Colors = { Color.green, Color.cyan, Color.yellow, new Color(1f, .5f, 0f), Color.red, Color.magenta };
        const float Distance = 2f;

        public static RpAngleProbe Create(Transform head)
        {
            var go = new GameObject("RP Angle Probe");
            go.transform.SetParent(head, false);
            var probe = go.AddComponent<RpAngleProbe>();
            probe.Build();
            return probe;
        }

        void Build()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            Dot(Vector3.forward * Distance, Color.white, shader);
            for (int r = 0; r < Rings.Length; r++)
                for (int m = 0; m < 8; m++)
                {
                    float az = m * 45f * Mathf.Deg2Rad, ecc = Rings[r] * Mathf.Deg2Rad;
                    // Direction at angular distance ecc from straight ahead, along meridian az (0 = right, 90 = up).
                    Vector3 dir = new Vector3(Mathf.Sin(ecc) * Mathf.Cos(az), Mathf.Sin(ecc) * Mathf.Sin(az), Mathf.Cos(ecc));
                    Dot(dir * Distance, Colors[r], shader);
                }
        }

        void Dot(Vector3 localPos, Color c, Shader shader)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(s.GetComponent<Collider>());
            s.transform.SetParent(transform, false);
            s.transform.localPosition = localPos;
            s.transform.localScale = Vector3.one * .02f; // ~0.57° at 2 m
            var mat = new Material(shader) { color = c };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            var r = s.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
