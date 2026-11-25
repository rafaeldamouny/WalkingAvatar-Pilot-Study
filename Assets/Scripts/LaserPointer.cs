using UnityEngine;

/// <summary>
/// Draws a controller laser pointer as a LineRenderer.
/// Attach to the right (or left) controller anchor transform.
/// Shortens when it hits geometry; stays full length otherwise.
/// </summary>
public class LaserPointer : MonoBehaviour
{
    [Tooltip("Beam colour.")]
    public Color BeamColor  = new Color(0.0f, 0.85f, 1.0f, 0.9f);
    [Tooltip("Max ray length in metres.")]
    public float MaxLength  = 8f;
    [Tooltip("Width at controller end.")]
    public float StartWidth = 0.006f;
    [Tooltip("Width at tip end.")]
    public float EndWidth   = 0.002f;

    LineRenderer _lr;

    void Awake()
    {
        _lr = gameObject.AddComponent<LineRenderer>();
        _lr.useWorldSpace    = true;
        _lr.positionCount    = 2;
        _lr.startWidth       = StartWidth;
        _lr.endWidth         = EndWidth;
        _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _lr.receiveShadows   = false;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Sprites/Default")
                  ?? Shader.Find("Unlit/Color");
        var mat = new Material(shader) { color = BeamColor };
        // Make it transparent-blend so it renders over geometry
        mat.SetFloat("_Surface", 1);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 4000;
        _lr.material = mat;
    }

    void Update()
    {
        Vector3 origin = transform.position;
        Vector3 dir    = transform.forward;

        float length = MaxLength;
        if (Physics.Raycast(origin, dir, out RaycastHit hit, MaxLength))
            length = hit.distance;

        _lr.SetPosition(0, origin);
        _lr.SetPosition(1, origin + dir * length);
    }
}
