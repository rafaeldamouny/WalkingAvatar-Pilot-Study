# RP visual-field simulation — Quest 3 + Neon XR

Scope: the simulation only (overlay shader, controller, field driver, and maps). Experiment flow, avatars, menus, and
the Neon data bridge remain in place. A dated pre-change copy of the edited scene and RP files is in
`IntegrationEvidence/backup_<timestamp>_rp_sim_before_stable_gaze/`.

## 1. What existed before (assessment)

| Component | Before |
|---|---|
| Rendering | Full-viewport overlay quad (`LowVisionOverlaySimple.shader`, queue Overlay+900, ZTest Always). Black with alpha over Unity content **and** the passthrough underlay (premultiplied-alpha compositing is enabled in `SeatedWalkingPlatform`). |
| Stereo | Per-pixel view rays from each eye's own matrices (`UNITY_MATRIX_I_P/I_V`), so the mask was computed per eye. |
| Field shape | A legacy virtual-plane circle and a newer angular field-map renderer were both present. The scene did not explicitly serialize which model to use. |
| Gaze contingency | Already gaze-centred via `NeonGazeBridge` → `RpFieldDriver` (one cyclopean direction for both eyes), adaptive smoothing, 0.12 s hold on sample loss, then head-centred fallback. |
| Size / edge | The menu selects a 15° or 30° diameter. The map boundary is the 50% visibility point, with a soft edge. |
| Logging | `ResearchSessionLogger` logs requested/effective centre, fallback, smoothing and overlay parameters per gaze sample. |

## 2. Neugebauer et al. (2024) — what they did vs what is added here

The 2024 comparison study measured each eye in the headset with kinetic perimetry (0.72° target, 3°/s, 24 meridians
spaced 15° apart), built a per-participant black mask with a transparent seeing region, softened its boundary over
about 2°, and moved it using eye tracking. Their headset was Pico Neo 2 Eye; the 79 ms latency cited in their paper
belongs to that Pico/Tobii system, not this Quest/Neon setup. Their VR perimetry was explicitly not diagnostically
validated. The study simulated field loss only, not contrast, acuity, glare, or night blindness.

The attached PLOS paper (doi:10.1371/journal.pone.0291902) is a VR gaze-training trial. Its 15° simulated tunnel
appears in a figure for illustration and was not used during participants' training. A separate 20° gaze-contingent
simulation is discussed for an earlier sighted-participant gaze-pattern experiment. The PLOS paper is useful for RP
training and mobility context, but is not the mask implementation reference.

Added here:
- **Exact visual angle**: eccentricity = angle between the view ray and the eye's gaze direction; meridian measured
  around that direction. Field size is correct at any gaze angle.
- **Fixed angular field**: the mask is evaluated by the angle between each view ray and the measured gaze direction,
  so changing the target's distance cannot change the field diameter.
- **Separate monocular centers when available**: use Neon dual-monocular gaze directions for each eye. If those are
  unavailable, use the binocular gaze direction for both eyes. Neon eye-state optical axes are not used as gaze:
  Pupil Labs documents them as vectors toward the pupil, distinct from its scene-camera gaze estimate.
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
- `usePerEyeMonocularGaze`: use separate dual-monocular gaze rays when Neon supplies them; otherwise binocular fallback.
- `smoothGazeWindow`: smooths the gaze center in head-local coordinates and reprojects it with the current head pose.
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
gaze usable/holding/blackout, loss policy, **gaze sample age at mask update**, whether dual-monocular centers were
used, cyclopean and per-eye gaze (head-local yaw/pitch), head yaw/pitch/roll. Clock: `unity_realtime_s`.

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

- **Blur and acuity loss are not possible with this overlay over native passthrough**: the overlay cannot read the passthrough
  image (it is composited underneath by the system). It can only darken or veil it. Acuity simulation would require
  camera-access passthrough rendered in Unity (Passthrough Camera API), with its own latency and resolution costs.
- Luminance-dependent (night-blindness) effects are not implemented.
- Latency: the log gives sample age at mask update; that is not end-to-end display latency. Measure the closed-loop
  delay on the actual Quest/Neon build with a high-speed recording or a validated display-change method. Published
  Quest 3 Neon gaze error is about 2.28° with the NeonXR baseline calibration and 1.73° with a separate research
  calibration method; this project does not implement that research calibration.
- The 15°/30° circles and example JSON fields are synthetic engineering presets, not clinical RP fields. Import
  participant-specific per-eye perimetry data before describing a map as personalized or representative.
- An accurate mask does not recreate the visual adaptation, gaze strategies, or mobility experience of RP. The
  Springer comparison found performance similarities in some virtual tasks but gaze-behavior differences, and it
  tested stationary participants in virtual scenes rather than walking in Quest passthrough.

## 8. Research and implementation references

- Neugebauer et al., *Simulating Vision Impairment in Virtual Reality* (field measurement and mask method):
  https://link.springer.com/article/10.1007/s10055-024-00987-0
- Neugebauer et al., *Influence of open-source virtual-reality based gaze training...* (RP training outcomes; its
  pictured tunnel simulation was not shown during training): https://doi.org/10.1371/journal.pone.0291902
- GazeQuest source repository (work in progress; its README lists Pico Neo 2 Eye as supported and Quest 3 as planned):
  https://github.com/ANCoral05/VR-GT---Virtual-Reality-Gaze-Training
- Pupil Labs Neon recording format (scene-camera gaze, eye-state fields and coordinate definitions):
  https://docs.pupil-labs.com/neon/data-collection/data-format/
- Pupil Labs Neon XR Unity integration:
  https://docs.pupil-labs.com/neon/neon-xr/
- Quest 3 Neon evaluation (baseline vs research calibration gaze accuracy):
  https://doi.org/10.1145/3806031
- Gaze-contingent display latency measurement method:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC4077667/
- A matching visual field does not reproduce the adapted gaze and navigation strategies of people with RP; that needs
  validation with RP participants.
