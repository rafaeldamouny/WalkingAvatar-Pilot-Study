# RP visual-field simulation (v2) — Quest 3 + Neon XR

Scope of this upgrade: **the simulation only** (overlay shader, overlay controller, field driver and new helper files in
`Assets/ExperimentIntegration/RP/`, maps in `Assets/ExperimentIntegration/Resources/RPFields/`). The experiment flow,
menus, figures, logging of trials and the Neon bridge are unchanged. The previous files are backed up in
`IntegrationEvidence/backup_2026-11-05_rp_sim_before/`.

## 1. What existed before (assessment)

| Component | Before |
|---|---|
| Rendering | Full-viewport overlay quad (`LowVisionOverlaySimple.shader`, queue Overlay+900, ZTest Always). Black with alpha over Unity content **and** the passthrough underlay (premultiplied-alpha compositing is enabled in `SeatedWalkingPlatform`). |
| Stereo | Per-pixel view rays from each eye's own matrices (`UNITY_MATRIX_I_P/I_V`), so the mask was computed per eye. |
| Field shape | One circle, defined on a virtual plane in "slope" (tangent) space: exact only when gaze is straight ahead; shrinks ~12% along the gaze axis at 20° gaze. |
| Gaze contingency | Already gaze-centred via `NeonGazeBridge` → `RpFieldDriver` (one cyclopean direction for both eyes), adaptive smoothing, 0.12 s hold on sample loss, then head-centred fallback. |
| Size / edge | `tunnelFovDegrees` (15°) at the 50% point of a 1.5° soft edge; clear interior. |
| Logging | `ResearchSessionLogger` logs requested/effective centre, fallback, smoothing and overlay parameters per gaze sample. |

## 2. Neugebauer et al. (2024) — what they did vs what is added here

They measured each eye with an in-VR kinetic perimetry test (white 0.72° target moving inward at 3°/s from 45°,
24 meridians at 15°), drew a black mask with a transparent field per participant in GIMP, blurred the edge over ~2°,
and moved it with the headset's eye tracking (Pico Neo 2 Eye, ~79 ms end-to-end latency). Binocular combination and
interpolation were not described; only the visual field was simulated (no acuity/contrast/glare).

Added here:
- **Exact visual angle**: eccentricity = angle between the view ray and the eye's gaze direction; meridian measured
  around that direction. Field size is correct at any gaze angle.
- **Per-eye fields and per-eye fixation**: each eye uses its own map and its own line of sight to the fixation point
  (vergence). Fixation distance comes from Neon eye-state data (eyeball centres + optical axes) when the Companion
  streams it; otherwise `defaultFixationDistance` (3 m).
- **Personalized maps** (JSON): 24-meridian boundaries (Neugebauer format), any number of meridians, residual islands,
  or a per-location sensitivity grid (dB) for static perimetry data. Per-device override without rebuilding.
- **Tracking-loss policy**: `HeadCentered` (original) or `BlackOut` (nothing visible until gaze returns, so tracking
  loss can never reveal the scene outside the simulated field).
- **Optional contrast veil** inside the field (off by default).
- **Simulation log** (per frame) and an **angle probe** for on-headset verification.

## 3. Settings

`LowVisionOverlayController` (on the overlay quad):
- `fieldModel`: `LegacyPlane` (original baseline, unchanged), `AngularCircular` (default; uses `tunnelFovDegrees` and
  `tunnelEdgeDegrees`), `PersonalizedMap`.
- `personalizedMapName`: map file name without `.json`.
- `contrastVeil` (0 = off), `veilLuminance`.

`RpFieldDriver`:
- `centerMode`: `HeadCentered` / `GazeCentered` (unchanged; the menu's eye-tracking choice still sets it).
- `perEyeFixation`, `defaultFixationDistance`, `vergenceFromDualRays` (uses eye-state data).
- `trackingLossPolicy`: `HeadCentered` (default, original behaviour) or `BlackOut`; hold time is `maximumInvalidHoldSeconds`.
- `writeSimulationLog` (default on), `showAngleProbe` (default off).

## 4. Visual-field map format

Files: `Assets/ExperimentIntegration/Resources/RPFields/<name>.json` (built in), or on the headset
`/sdcard/Android/data/<package>/files/RPFields/<name>.json` (overrides the built-in file; push with
`adb push P012.json /sdcard/Android/data/<package>/files/RPFields/`).

Coordinates are the participant's own visual field of **each eye**, around fixation: azimuth 0° = right, 90° = up,
180° = left, 270° = down; eccentricity in degrees. (Right eye: 0° is temporal. Left eye: 0° is nasal.)

```json
{
  "id": "P012_2026-11-20",
  "synthetic": false,
  "description": "Kinetic VR perimetry, 24 meridians",
  "source": "lab measurement 2026-11-20", "measuredWith": "VR kinetic perimetry (not diagnostically validated)",
  "edgeDeg": 2.0,
  "left":  { "radiiDeg": [24 values, meridians 0,15,...,345], "islands": [ {"azimuthDeg": 210, "eccentricityDeg": 32, "radiusDeg": 5} ] },
  "right": { "meridiansDeg": [optional explicit angles], "radiiDeg": [...] }
}
```
- One radius = circle. Radii are the 50% point of the soft edge (`edgeDeg` total width).
- Sensitivity instead of a boundary: `"points": [{"xDeg": -3, "yDeg": 3, "db": 24}, ...]` per eye, with
  `dbBlind` (→ invisible), `dbFull` (→ fully visible) and `interpolationRadiusDeg` (Gaussian-weighted, σ = radius/2;
  locations with no measured point nearby are not seen). The dB→visibility mapping is a modelling assumption and
  should be chosen with the clinicians.
- `"synthetic": true` marks invented test fields. **Only maps derived from real measurements may set `false`.**
  The included maps are synthetic: `synthetic_circular_15deg`, `synthetic_irregular_example`,
  `synthetic_sensitivity_example`.

## 5. Logs

`/sdcard/Android/data/<package>/files/RPSimulation/rp_<date>.csv`, one row per frame (`params_changed` rows mark
parameter changes): vision type, field model, map id/synthetic/origin, size, edge, veil, requested/effective centre,
gaze usable/holding/blackout, loss policy, **gaze sample age at mask update**, fixation distance, cyclopean and per-eye
gaze (head-local yaw/pitch), head yaw/pitch/roll. Clock: `unity_realtime_s` (same as the research log).

## 6. Verification (do on the Quest; editor tests do not cover the compositor or Neon)

1. **Size**: `centerMode = HeadCentered`, `showAngleProbe = true`, 15° field. Look straight ahead: 2.5° and 5° dots fully
   visible, 7.5° dots half-faded (edge midpoint), 10°+ dots hidden. Repeat with each eye closed.
2. **Passthrough**: real objects outside the field must be black, not only virtual ones.
3. **Gaze following** (Neon, verified mount calibration): fixate a 10° dot; it becomes visible and the centre dot
   disappears. Check all 8 directions.
4. **Saccades / tracking loss**: rapid eye movements, blinks, lifting the headset briefly. With `BlackOut` nothing
   outside the field may flash. Check `gaze_sample_age_s` in the log.
5. **Personalized map**: load `synthetic_irregular_example`; the island should appear temporally, lower in each eye.
6. **Performance**: frame rate unchanged (one texture lookup per pixel).

## 7. Limitations

- **Blur and acuity loss are not possible with this overlay over passthrough**: the overlay cannot read the passthrough
  image (it is composited underneath by the system). It can only darken or veil it. Acuity simulation would require
  camera-access passthrough rendered in Unity (Passthrough Camera API), with its own latency and resolution costs.
- Luminance-dependent (night-blindness) effects are not implemented.
- Latency: the log gives sample age at mask update; network transfer and display latency are not included. End-to-end
  latency needs a photodiode or high-speed-camera measurement. Published NeonXR accuracy on Quest 3 is ~1.7–2.3°,
  which is large relative to a 15° field — report the verified accuracy for each session.
- Per-eye fixation assumes the eyes converge on a point along the cyclopean gaze direction; ocular torsion is ignored.
- A matching visual field does not reproduce the adapted gaze and navigation strategies of people with RP; that needs
  validation with RP participants.
