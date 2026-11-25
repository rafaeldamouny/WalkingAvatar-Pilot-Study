# Seated walking avatar + RP + Pupil Labs Neon

Working project: `C:\Unity\FootstepsPredictions1_PupilLabs`, Unity **6000.2.4f1**.
`C:\Unity\AdaptiveVis` is a read-only source. It was never opened in Unity during integration.

## Open and run

Open `Assets/Scenes/NeonWalkingExperiment.unity`. This is the enabled build scene.
Press Play for the avatar platform, or use **Experiment Integration → Build Quest APK**.
The avatar uses the existing `casual-f-0170 1.prefab`, the existing humanoid Avatar asset,
`MotusMan Animator Controller`, and `SimpleWalkDriver`. The participant remains seated. The app opens
with a head-locked menu: right thumbstick left/right changes `P###` by 1, up/down changes it by 10,
B switches the tunnel between 15° and 30°, and A starts once valid Neon gaze is available. Start
applies the chosen FoV before opening that participant's CSV, then places the avatar
two meters ahead facing away, and begins its six-meter walk. Menu/status/debug visuals and all text are
hidden for the trial; controller changes are disabled. The log closes when the avatar reaches its goal.
No MRUK room scan is required.
Set **Seated Walking Experiment → SeatedWalkingPlatform → Seated Eye Height** to the
participant's actual eye-to-floor height before running.

## Installed packages and files

- Existing URP **17.2.0**, OpenXR **1.15.1**, Meta XR **83.0.1**, Input System **1.14.2** retained.
- Official Neon XR Core **2.0.0** embedded at `Packages/com.pupil-labs.neon-xr.core`.
  Upstream commit: `9f06486fb1cd9a5ce2f5b242101622712b18e506`.
  Local vendor extension: two read-only getters, `CurrentGazeData` and `SimulationEnabled`,
  expose the exact processed snapshot and identify SDK simulation. These are documented local
  additions; the listener uses the original `gazeDataReady` event. Native networking/mapping code is retained.
  The bundled `pl.config` mount offset now contains the user's selected first saved calibration;
  the original SDK configuration is preserved in `IntegrationEvidence/sdk-config-before-gaze-activation.json`.
- Unity resolved Neon dependencies to Addressables **2.7.2** and XR Interaction Toolkit **3.2.1**.
- `Assets/AddressableAssetsData` includes `pl.config`, `pl.config.defaults`, and `pl.calibration`.
  The Quest build command builds these locally before the APK.
- `Assets/ExperimentIntegration/RP`: source RP shader/material/controller, adapted in the working copy.
- `Assets/ExperimentIntegration/Runtime`: gaze bridge, animated anatomical proxies, seated platform, CSV logger.
- `Assets/ExperimentIntegration/Editor`: scene preparation, checks, Quest build commands.
- `IntegrationEvidence`: source originals, compile logs, dependency inventory, validation results.

The existing Meta Movement SDK dependency initially points to
`C:/Users/AmplitLab/Downloads/Unity-Movement-83.0.0/Unity-Movement-83.0.0`.
Consult `Packages/manifest.json` for the final package location. Do not delete that source folder if the manifest still points to it.

## Physical setup: Neon + Samsung Galaxy S25 + Quest 3 mount

1. On the **S25**, install/update **Neon Companion** from Google Play using the link on
   [Pupil Labs' supported Companion devices page](https://docs.pupil-labs.com/neon/hardware/compatible-devices/).
   Open it, complete its setup, and grant its requested device/USB permissions.
2. Fit the official Quest 3 mount using its supplied instructions. Pupil Labs specifies
   **mount level 3**. Connect the Neon module's USB-C cable to the **phone**.
   [Official hardware/network overview](https://docs.pupil-labs.com/neon/neon-xr/).
3. Connect the S25, Quest, and laptop to the same local network without client isolation.
   Keep Neon Companion running. Select **Streaming** on its home screen and verify its
   live gaze display/monitor works before troubleshooting Unity.
   [Monitor instructions](https://docs.pupil-labs.com/neon/data-collection/monitor-app/).
4. In Companion, choose **Binocular** as the primary gaze mode for combined gaze.
   Set `NeonGazeBridge.companionGazeMode` to `Binocular` after checking this setting.
   The Unity API does not identify which phone primary-mode setting was selected.
   [Gaze-mode documentation](https://docs.pupil-labs.com/neon/data-collection/gaze-mode/).
5. Enable Quest Developer Mode and USB debugging, connect the headset to the laptop,
   and accept its USB debugging prompt. Use the helper commands in `Tools/QuestSetup.ps1`.

The laptop does **not** need Pupil Capture or the legacy hmd-eyes plugin. Unity uses the official
[Neon XR Core package](https://docs.pupil-labs.com/neon/neon-xr/neon-xr-core-package/).

## Mount calibration: required before research use

The ordinary Neon gaze estimator and the headset mount calibration are separate procedures.
The current configuration contains the user's selected first saved mount calibration. The user
reported a consistent downward pointer offset of approximately 2–3 cm, preferred this calibration
to a second attempt, and explicitly requested tunnel activation. `mountCalibrationVerified` is
enabled for this manual validation build, with the known offset in `calibrationId` and condition
`RP_gaze_contingent_passthrough`. The calibration ID records user acceptance for testing; measured research accuracy
remains unverified. Both saved configurations are preserved in `Calibration`. The original
integration configuration had this flag disabled; the following procedure applies to new calibrations.

1. Download the **Quest 3 MRTK3 Template APK** from the link in the
   [official template instructions](https://docs.pupil-labs.com/neon/neon-xr/MRTK3-template-project/).
2. Install that APK on Quest with the helper. Launch it under **Unknown Sources**.
3. Navigate to **PL_Calibration** using the template's palm-up menu. Look at each target;
   confirm using the specified pinch gesture. Select **Save** when complete.
   [Official mount calibration procedure](https://docs.pupil-labs.com/neon/neon-xr/build-your-own-mount/).
4. Copy the template's saved `config.json` from
   `/sdcard/Android/data/org.MixedRealityToolkit.MRTK3Sample/files/config.json`
   into this application's files directory:
   `/sdcard/Android/data/com.amplitlab.neonwalking/files/config.json`.
   First launch this app once to create its directory, close it, copy the file, then relaunch.
   The helper includes **CopyMountCalibration**. Preserve the SDK's calibration file; do not replace it with arbitrary data.
5. Validate the calibrated pointer on central and peripheral targets in the template and this app.
   Set **NeonGazeBridge → Mount Calibration Verified** true and give **Calibration Id** a meaningful
   identifier in Unity; rebuild/reinstall the app. This flag is an experimenter confirmation,
   not an automatic accuracy check. Revalidate after the headset/mount fit changes.

If the template's paths differ in your installed version, inspect its actual package/files path
before copying. Do not guess calibration numbers.

## Network configuration

On first use the SDK creates `config.json`, `configDefaults.json`, and `calibration.bin`
in `Application.persistentDataPath`, using the bundled Addressables.
It attempts automatic discovery by default. The **actual inspected source** uses the JSON key
`rtspSettings.autoIp` (capital I, lowercase p), although the documentation table calls it `autoIP`.
For manual connection, set `autoIp` false and `ip` to the S25 address shown by Companion.
Keep `useUdp` true for the native timestamped RTSP client. The default port is **8086**.
Edit/push the app's persistent `config.json`; editing bundled defaults does not overwrite an existing file.
Restart the app after changing connection or calibration files.

## RP and gaze debug controls

- **B** on the right controller switches **HeadCentered / GazeCentered**.
- Right stick click toggles the gaze ray/hit sphere. A toggles walking.
- Editor keyboard: **G** switches center, **V** toggles gaze debug, **Space** pauses/resumes, **R** restarts.
- Inspector: `RP Visual Field → RpFieldDriver → Center Mode`.
- Set `NeonGazeBridge → Use Synthetic Gaze` for explicit editor testing; adjust
  **Synthetic Angles Degrees** while keeping the head fixed. This is not device eye tracking.
  Synthetic data is excluded from normal gaze logging unless **Allow Synthetic Recording** is enabled.
- `LowVisionOverlayController` preserves the source's None/Cataract/Glaucoma/Severe presets;
  **Tunnel Fov Degrees** defaults to the source's 10°. Zero uses the Inspector radius.
  Use **Apply Inspector Settings** or edit live Inspector values to reapply parameters.
- To check pointer calibration without impairment, set vision type **None** and reapply.
  Hide **Show Status** and turn off gaze debug for research trials after validation.

The source shader's vignette/color/opacity equations are reused. Its "blur" is a color/opacity
approximation, not a scene-image blur. The original Glaucoma preset darkens even the residual region
by 85%; that value is intentionally retained. The URP adapter reconstructs a ray for each eye and
offsets the field using calibrated gaze direction in head coordinates. It fills the whole eye viewport,
supports stereo instancing, and writes framebuffer alpha to mask the native passthrough underlay.
Camera clear is transparent black, framebuffer alpha is preserved, post-processing alpha output is
enabled, and camera HDR is disabled. The virtual floor renderer is hidden. Alpha-over blending
produces premultiplied framebuffer RGB; the eye compositor is explicitly configured to match.
Inside the opening the existing 85% RP dimming also applies to the real room. Outside it alpha is
one and RGB is black, hiding both real and virtual content. Status/debug aids render before the mask.
Vision type None restores the unmasked room and avatar for calibration checks.
See [Meta's passthrough guide](https://developers.meta.com/horizon/documentation/unity/unity-passthrough-gs/)
and [Unity's framebuffer alpha setting](https://docs.unity3d.com/6000.2/Documentation/ScriptReference/PlayerSettings-preserveFramebufferAlpha.html).
Quest visual checks remain essential; native passthrough is not present in editor screenshots.
It currently applies identical source impairment to both eyes; this is not a clinical per-eye RP model.

## Avatar anatomical regions

`AvatarRegionRig` builds bone-following sphere/capsule proxies for **Head, UpperTorsoShoulders,
Pelvis, LeftArm, RightArm, LeftLeg, RightLeg, LeftFoot, RightFoot**.
They do not affect walking physics. Explicit nearest-collider raycasts exclude ordinary avatar
colliders and account for opaque environment occlusion. Physics transforms are synchronized
after animation before sampling. Enable **Show Proxies** for Scene-view outlines.
Adjust proxy radii for each exemplar and visually validate boundaries before collecting research data.
These colliders approximate anatomy; they do not segment the rendered skin mesh pixel by pixel.

## CSV logs and interpretation

Saved under **`Application.persistentDataPath/ResearchLogs`**. On Quest:
`/sdcard/Android/data/com.amplitlab.neonwalking/files/ResearchLogs/`.
In the editor the exact path is printed as `[ResearchLogs] CSV: ...`.
Use the helper's **PullLogs** command to copy files into `ResearchExports` in this project.

CSV uses invariant numeric formatting, UTF-8, quoted text escaping, unique session filenames,
and separate `session_start`, `trial_start`, `gaze_sample`, `tracking_status`, `response`,
`trial_end`, and `session_end` rows. It flushes approximately once a second and on pause,
responses, trial boundaries, and shutdown. Forced process termination can lose the last unflushed second.
Recording errors stop logging and appear in the headset status/Unity console.

Schema **3** has 116 columns, adding invalid-hold duration and effective states HeldGaze and
HeadCenteredTransition to the display telemetry introduced in schema 2.
The adaptive low-pass uses a 2 Hz minimum frequency, 0.03 speed coefficient (angular speed in
degrees per second), and an 8 Hz signed velocity filter. Filtering velocity before taking its magnitude
allows alternating noise to cancel. It smooths only the displayed RP opening, once per
frame in head-local coordinates, and reprojects it with the latest head pose before rendering.
Invalid gaze holds the last displayed head-local position for up to 120 ms, then returns to head center
using the minimum-frequency filter without speed acceleration. Recovery continues the existing filter
at the minimum frequency for 150 ms before restoring speed adaptation, avoiding a large first step
when tracking returns after a longer loss. This fixed recovery interval is part of schema 3 semantics.
Invalid research samples are never reclassified as valid or used for anatomical hits during the hold.
It resets on explicit head-centered mode and application pause. It adds temporal lag without
changing the steady gaze gain. Raw gaze rays and anatomical hit detection remain unfiltered.
Gaze rows are written in LateUpdate after the display filter; later before-render head reprojection
can update its world direction without another CSV row. The active Glaucoma blur setting is now
3.6, reduced 40% from the original 6; other source presets retain their values.

All requested metadata is included. Positions/directions are XYZ columns; head rotation is XYZW quaternion.
Unity distances are meters, headings degrees, cutoff/response times seconds. Empty fields mean unavailable,
not zero. `cutoff_time`, response/correct-response fields remain empty until experiment code supplies them.

- `timestamp`: laptop/headset UTC when Unity logs the row, **not hardware capture time**.
- `sdk_timestamp_ms` and `sdk_rtcp_synchronized`: SDK timestamp/clock-sync status, preserved separately.
  Do not assume unsynchronized SDK timestamps share the UTC clock.
- `unity_realtime_s`, sequence, frame and sample-age fields document reception timing.
- Head/avatar poses are the Unity pose at consumption; delayed samples are not retrospectively
  aligned to capture-time head pose. Measure end-to-end latency and add pose history/clock alignment
  before capture-time precision or T0 analysis is required.
- The official provider consumes its latest network snapshot once per Unity Update. These logs
  describe displayed/consumed gaze, **not every 200 Hz phone sample**. Run a parallel Companion
  recording when full-rate raw gaze is needed.
- Validity combines fresh finite gaze, `worn`, and experimenter-confirmed mount calibration;
  it is not a numerical tracking-confidence estimate or a guaranteed blink detector.
  `gaze_confidence` is empty and `gaze_confidence_available=0`: this SDK has no confidence value.
- The calibrated ray origin is the SDK's Neon **scene-camera/sensor origin proxy**, not a measured
  cyclopean eye center. Combined direction is the SDK primary gaze signal. Dual-only packets use
  the normalized mean of two valid mapped directions from the same sensor-origin proxy.
- Dual-monocular gaze rays are logged only when the packet type provides them. They use the
  common calibrated sensor origin; do not interpret them as measured left/right eyeball-origin rays.
- Eye-state centers, optical axes, pupil diameter and eyelid measurements are logged only if present.
  The inspected `RTSPServiceWrapper.BytesToGazeData` converts pupil diameter/centers from mm to meters,
  flips eye-state Y into Unity's sensor basis, and converts eyelid angles from radians to degrees.
  The logger preserves sensor centers in meters, converts pupil diameter back to mm and angles back
  to radians, and retains aperture in mm (the wrapper does not scale aperture). Sensor optical axes
  are **not calibrated visual gaze axes**, and are never used for research gaze raycasts.

Future trials can call `ResearchSessionLogger.BeginTrial(id, condition, exemplar, cutoff, expectedResponse)`,
`RecordResponse(response)`, and `EndTrial()`. The platform does not implement Left/Straight/Right mocap
cutoffs or T0 analysis yet.

## Validation and next hardware step

See `IntegrationEvidence` for actual compile/test/build outcomes. An editor synthetic-gaze check
does not verify Neon networking, headset calibration, stereo appearance, or on-device performance.
Before participant collection, verify both eyes separately, hold the head still and move the eyes,
switch B between centering modes, inspect anatomical hits, disconnect Neon to verify fallback,
and confirm real timestamps/validity/body-region values in the pulled CSV.

Scene preparation commands regenerate the integration scene. **Prepare RP Scene**, then
**Prepare Complete Platform** rebuild it from the preserved original Working scene and reset
calibration verification. Do not run them on a calibrated customized scene unless you intend to regenerate it.
