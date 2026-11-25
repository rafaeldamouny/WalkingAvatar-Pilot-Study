// Shared retinitis pigmentosa visual-field model (irregular island), used by:
//   RPIslandMask.shader       - black mask over system passthrough
//   RPCameraIsland.shader     - camera feed shown only inside the island
//
// RPIslandVisibility(d, eye) returns 1 where the simulated eye still sees, 0 where it is blind and
// values in between at the soft border. 'd' is the view direction in that eye's gaze space (+Z = gaze).
//
// Shape notes:
//  * Outline noise is deliberately low-frequency (2 smooth octaves). Fine detail made the border look
//    spiky; real field borders are measured with stimuli of 0.4-1.7 deg, so nothing finer is known.
//  * The border fades over about 2 x _Feather degrees (published VR RP simulations use ~2 deg).
//  * Both eyes get nearly the same shape: RP fields are highly symmetric between eyes (r ~ 0.97).
#ifndef RP_ISLAND_INCLUDED
#define RP_ISLAND_INCLUDED

float4x4 _HeadToGazeEye[2];   // per-eye rotation: head space -> gaze space (handles convergence)
float _Radius, _Aspect, _Warp, _Lobes, _Feather, _Mottle;
float4 _CenterOffset;
float _OuterStart, _Residual, _CrescentStart, _Seed, _EyeSeedOffset;

float RPHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float RPValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float a = RPHash(i);
    float b = RPHash(i + float2(1, 0));
    float c = RPHash(i + float2(0, 1));
    float d = RPHash(i + float2(1, 1));
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic: no creases
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// Smooth 2-octave noise in [0, 1] - used for everything that shapes the outline.
float RPSmoothNoise(float2 p)
{
    return (RPValueNoise(p) + 0.5 * RPValueNoise(p * 2.03 + 17.0)) / 1.5;
}

// 4-octave noise in [0, 1] - only for the scattered residual spots.
float RPFbm(float2 p)
{
    float v = 0.0;
    float amp = 0.5;
    [unroll] for (int k = 0; k < 4; k++)
    {
        v += amp * RPValueNoise(p);
        p = p * 2.03 + 17.0;
        amp *= 0.5;
    }
    return v / 0.9375;
}

float RPSmoother(float e0, float e1, float x)
{
    float t = saturate((x - e0) / max(e1 - e0, 1e-5));
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

// Degrees of visual angle relative to the gaze direction.
float2 RPGazeDegrees(float3 d)
{
    return degrees(float2(atan2(d.x, d.z), atan2(d.y, d.z)));
}

float RPIslandVisibility(float3 d, int eye)
{
    if (d.z <= 0.05)
    {
        return 0.0;
    }

    float seed = _Seed + eye * _EyeSeedOffset;
    float R0 = max(_Radius, 0.5);
    float invR = 1.0 / R0;
    float2 deg = RPGazeDegrees(d);

    // 1) Domain warp: bends the outline into a blob (scaled with the island size).
    //    Single octave at a low frequency: a stronger or finer warp can fold the outline over itself,
    //    which shows up as spikes and notches.
    float2 wp = deg * invR * 0.45;
    float2 warp = float2(RPValueNoise(wp + seed * 13.1), RPValueNoise(wp + seed * 29.7 + 5.2)) * 2.0 - 1.0;
    float2 p = deg + warp * (_Warp * R0) - _CenterOffset.xy;
    p.x /= max(_Aspect, 0.1);
    float r = length(p);

    // 2) Uneven lobes: radius varies with direction (noise sampled on a circle, so it wraps).
    float2 cs = r > 1e-4 ? p / r : float2(1, 0);
    float lobe = RPValueNoise(cs * 1.0 + seed * 7.3) * 2.0 - 1.0;
    float R = R0 * (1.0 + _Lobes * lobe);

    // 3) Soft border whose width varies gently around the outline (no high-frequency jitter).
    float f = max(_Feather, 0.05) * (0.85 + 0.3 * RPValueNoise(cs * 1.0 + seed * 3.1));
    float vis = 1.0 - RPSmoother(R - f, R + f, r);

    // 4) Patchy sensitivity loss towards the island border: broad patches that only dim, never
    //    cut all the way to black (that produced notches in the outline).
    float patchNoise = RPValueNoise(deg * invR * 0.8 + seed * 5.9);
    vis *= lerp(1.0, 0.45 + 0.55 * patchNoise, _Mottle * smoothstep(R * 0.45, R, r));

    // 5) Ring scotoma: vision returns in the far periphery (earlier stages).
    if (_OuterStart > 0.0)
    {
        float outerLobe = RPSmoothNoise(cs * 1.1 + seed * 2.3) * 2.0 - 1.0;
        float Ro = _OuterStart * (1.0 + 0.6 * _Lobes * outerLobe);
        vis = max(vis, RPSmoother(Ro - f, Ro + 2.0 * f, r));
    }

    // 6) Small residual islands scattered in the blind area.
    if (_Residual > 0.0)
    {
        float n = RPFbm(deg * 0.11 + seed * 11.0);
        float t = 1.0 - _Residual * 0.3;
        vis = max(vis, smoothstep(t, t + 0.06, n) * 0.85);
    }

    // 7) Temporal crescent (left eye: left side, right eye: right side).
    if (_CrescentStart > 0.0)
    {
        float tx = eye == 0 ? -deg.x : deg.x;
        float cn = (RPSmoothNoise(deg * 0.08 + seed * 4.4) - 0.5) * 12.0;
        float band = smoothstep(_CrescentStart, _CrescentStart + 6.0, tx + cn);
        band *= 1.0 - smoothstep(20.0, 35.0, abs(deg.y) + cn);
        vis = max(vis, band * 0.9);
    }

    return saturate(vis);
}

#endif
