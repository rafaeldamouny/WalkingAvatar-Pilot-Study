# Neon seated walking experiment

Open **FootstepsPredictions1_PupilLabs** with **Unity 6000.2.4f1**, then open
**Assets/Scenes/NeonWalkingExperiment.unity**. All changes are inside this working copy.
**AdaptiveVis remains read-only.**

## Platform

The scene retains the existing `casual-f-0170 1` avatar, humanoid rig, MotusMan walking
controller, SimpleWalkDriver, and Meta/OpenXR camera rig. It adds the source RP effect,
official Neon XR Core package, gaze control, anatomical proxies, and CSV logging.
The participant is seated; after Start, the avatar appears at **2 m** and begins walking away.
Set **SeatedWalkingPlatform → Seated Eye Height** to the participant's physical eye-to-floor height.

The deployed experiment opens with a headset menu and does not spawn the avatar or record data yet.
Use the **right thumbstick** left/right to change the participant number by 1 and up/down to change it
by 10. The ID is displayed as `P###`. Once the menu reports **NEON READY**, press right **A** once.
Press right **B** before Start to switch between **15°** and **30°**; the selected option is bracketed.
That action starts a uniquely named participant CSV, spawns the existing avatar at 2 m, and starts it
walking. The chosen tunnel value is recorded in `rp_tunnel_fov_deg`. During the trial the menu,
setup status, gaze ray and every headset text element are hidden.
Controller changes are disabled during the trial. When the avatar reaches the endpoint, the trial and
CSV session are closed. Relaunch the app for the next participant.

The real room is shown through Quest passthrough behind the avatar. Both are visible only through
the gaze-contingent opening; the outer field is opaque black. The virtual floor is hidden.
The source RP dimming inside the opening is retained. Setup status and gaze markers also obey the mask;
temporarily select vision type **None** for unobstructed calibration or connection checks.

Current manual validation configuration uses the first saved Quest mount calibration, selected by
the user after the second calibration was less accurate. Gaze-centered RP is enabled at the user's
request. A consistent downward pointer offset of about 2–3 cm was reported; angular accuracy has
not been measured. The calibration ID records this limitation, and the experiment condition is
`RP_gaze_contingent_passthrough`. Both calibration files are preserved in **Calibration**.

- `Assets/ExperimentIntegration/RP`: reused RP material/controller/shader, adapted for URP stereo.
- `Assets/ExperimentIntegration/Runtime`: experiment platform, Neon bridge, anatomical hits, logging.
- `Assets/ExperimentIntegration/Editor`: preparation, validation, Quest build commands.
- `Packages/com.pupil-labs.neon-xr.core`: installed official SDK, version 2.0.0.
- `Assets/AddressableAssetsData`: bundled SDK configuration/calibration assets.
- `IntegrationEvidence`: compile/test/build outcomes and preserved source files.

Required packages: URP 17.2.0, OpenXR 1.15.1, Meta XR 83.0.1, Input System 1.14.2;
Neon dependencies resolved to Addressables 2.7.2 and XR Interaction Toolkit 3.2.1.
See **INTEGRATION_DETAILS.md** for SDK provenance, exact dependencies and limitations.

## Phone and headset setup

1. On the **Galaxy S25**, install/update **Neon Companion** from the Google Play link on
   [Pupil Labs' Companion page](https://docs.pupil-labs.com/neon/hardware/compatible-devices/).
   Complete setup and grant its requested USB/device permissions.
2. Install the official Quest 3 mount according to its supplied instructions, at **level 3**.
   Connect Neon by USB-C to the **phone**. Keep Companion running.
3. Put phone, Quest and laptop on the same local network. In Companion, open **Streaming**
   and verify its live gaze display. Choose **Binocular** as the primary gaze mode.
4. Enable Quest Developer Mode; connect it by USB and accept USB debugging.
   Use `Tools/QuestSetup.ps1 -Action InstallExperiment` to install **Builds/NeonWalkingExperiment.apk**.
   For subsequent builds, **Experiment Integration → Build Quest APK** builds the app and required
   Addressables. The laptop helper `Tools/BuildQuest.ps1` also builds them and supplies a Windows
   temporary directory to the Android tools for this environment.
5. Download/install the official Quest 3 demo APK linked in the
   [Neon template instructions](https://docs.pupil-labs.com/neon/neon-xr/MRTK3-template-project/).
   Launch it under **Unknown Sources**, open **PL_Calibration** using its palm-up menu,
   look at each target, confirm with a pinch, then **Save**.
6. Launch this experiment once, close it, then run
   `Tools/QuestSetup.ps1 -Action CopyMountCalibration`. Relaunch it and inspect the pointer.
   An unverified pointer is available for calibration checking, but cannot control RP or count as valid gaze.
7. After verifying accuracy, set **NeonGazeBridge → Mount Calibration Verified** true,
   set **Calibration Id**, and set **Companion Gaze Mode** to `Binocular`. Rebuild/reinstall.

The calibration flag is your confirmation of measured accuracy; it does not automatically test calibration.
In the current manual validation configuration, the flag was enabled by explicit user instruction
despite the reported residual offset; it does not establish research accuracy.
Revalidate after fit/mount changes. See [official mount calibration](https://docs.pupil-labs.com/neon/neon-xr/build-your-own-mount/).
If discovery fails, use the helper's **SetConnection** action with the S25 IP shown by Companion.
Detailed commands and persistent file paths are in **INTEGRATION_DETAILS.md**.

## Controls and RP settings

- Right **A**: walk/pause/resume; after reaching the end, start again.
- Before Start, right **B** switches the tunnel between **15°** and **30°**.
- During the deployed experiment, participant controller changes are disabled.
- Inspector: **RP Visual Field → RpFieldDriver → Center Mode**.
- **RpFieldDriver → Smooth Gaze Window** filters the displayed opening in head-local coordinates.
  Minimum frequency **2 Hz** reduces fixation jitter; speed coefficient **0.03** increases responsiveness
  during large eye movements. Lower minimum frequency gives stronger smoothing and more lag.
  Signed velocity filtering reduces sensitivity to alternating noise. The filter preserves the final gaze location.
  Invalid tracking holds the last window position for up to **120 ms**, then eases toward head center.
  Recovery uses gentle smoothing for **150 ms** before restoring speed adaptation.
  Explicit head-centered mode remains immediate. Raw invalid samples stay invalid.
- **LowVisionOverlayController** retains source presets. The menu defaults to **Glaucoma, 15°** and allows **30°** to be selected before Start.
  The active Glaucoma blur setting is **3.6**, reduced 40% from the original **6** at the user's request.
  Edit its settings and use **Apply Inspector Settings**; zero FoV uses the Inspector radius.
  Set vision type **None** for unobstructed calibration checking.
- **NeonGazeBridge → Use Synthetic Gaze / Synthetic Angles Degrees** is an explicit editor test.
  Synthetic samples are excluded from ordinary research gaze logging.

Without fresh, worn, calibrated gaze, the requested gaze-centered mode falls back to head-centered.
Headset status identifies the current tracking state. Hide **Show Status** and disable visualization after validation.
Anatomical sphere/capsule proxies follow animated bones for head, torso/shoulders, pelvis, both arms,
both legs, and both feet. Inspect **AvatarRegionRig → Show Proxies** and tune radii per exemplar.

## Logs and later trials

CSV files are saved at **Application.persistentDataPath/ResearchLogs**. Filenames begin with the
selected participant ID, for example `P042_session_...csv`. The editor prints the exact path.
Quest path: **/sdcard/Android/data/com.amplitlab.neonwalking/files/ResearchLogs/**.
`Tools/QuestSetup.ps1 -Action PullLogs` copies them into this project's **ResearchExports** folder.

Logs include experiment metadata, UTC/SDK/reception timestamps, avatar/head poses, gaze rays,
validity, requested/effective RP mode, hits/body regions, responses and available per-eye measurements.
CSV schema **3** records smoothing/hold settings and the applied head-local window direction/frame.
Effective mode can also be **HeldGaze** or **HeadCenteredTransition** during invalid tracking.
Raw gaze rays and anatomical hits are not smoothed; gaze sample rows are written after the window's
LateUpdate filter. Later before-render head-pose reprojection is not retrospectively logged.
Numeric values use invariant formatting; unavailable values remain empty. Confidence is empty because
the inspected SDK provides no numerical score. The SDK's calibrated sensor origin is logged as an
origin proxy, rather than a measured eye center. Unity logs consumed samples at its frame rate;
record in Companion in parallel for full-rate raw data. Capture-time pose alignment is not implemented yet.

Use **ResearchSessionLogger.BeginTrial**, **RecordResponse**, and **EndTrial** for future mocap conditions.
Final direction/cutoff/T0 trials are intentionally deferred.

## Hardware verification required

Local validation on 15 September 2026: **39 checks passed before this smoothing revision**, covering
passthrough-mask alpha, two-meter paused spawn, walking animation, gaze centering, anatomical hits,
invalid/stale gaze rejection, rendered mono previews and CSV units. The revised filter passed
**9 focused checks**, including stronger jitter rejection and tracking loss/recovery behavior; see
**IntegrationEvidence/window-smoothing-checks.txt**. The full play-mode rerun stalled twice after
walking began and was stopped; **IntegrationEvidence/smoothing-checks-editor.log** preserves that run.
The final **Quest APK build succeeded with zero errors**; its ARM64 Neon/OpenXR libraries and bundled
SDK Addressables were checked. AdaptiveVis's 318 Assets/Packages/ProjectSettings files match their
pre-integration SHA-256 hashes. Consult **IntegrationEvidence/platform-checks.txt**, **android-build.txt**
and **apk-package-verification.txt** for evidence.

Editor checks do not establish hardware tracking or stereo appearance. On Quest, check each eye
separately, move the eyes while holding the head still, compare B modes, inspect body hits, unplug
Neon to verify fallback, and pull a CSV to confirm real timestamps/validity. Hardware testing requires your devices.

Latest Quest run: the updated app installed and launched, the native passthrough layer was created
and resumed, and the pulled CSV contained 3,515 real gaze sample rows, including 2,552 valid rows.
The latest sample was worn and gaze-centered. See **IntegrationEvidence/passthrough-live-verification.txt**.
Room visibility within the opening and stereo appearance still require participant visual confirmation.
