# Perception Pilot – current app mode and future mocap setup

## Try the complete sequence in the app now

In the **Walking Study** menu, leave eye tracking on **Headset only** if Pupil Labs is unavailable. It opens on
**Full Experiment**; the **left thumbstick click** switches between it and **Walk Preview**. Press **A** to start.
Pull the
right trigger to set the standing position and straight-ahead direction. The right trigger starts each block and
records a pointing response; **A/B** report stairs up/down. Hold **left Y** for 1.5 s to reset to the menu (one press once the session has finished).

**Editor keyboard (no headset):** M = preview/full, Enter or A = start, arrows = participant, C = preview avatar,
S = tunnel simulation, T = eye-tracking source, Space = trigger, ←/↑/→ = left/straight/right, U/D = stairs,
Backspace = reset to the menu. Turn off the Console's **Error Pause**: Meta XR's editor-window errors otherwise pause Play mode, and keys then do nothing.

**Rendering rule:** head-locked menus and prompts use render queue 5000 so they draw over the tunnel overlay (4900).
Never use a queue above 5000: URP renders transparent queues 2501–5000 only, so text at 5001 is never drawn.

The menu's **Tunnel simulation** switch controls whether tunnel-vision blocks actually apply the overlay. The two
avatar conditions are the realistic avatar and a point-light preview. The point lights are 19 small, white, unshaded
landmark dots attached to the animated avatar's joints. They use the existing animation, so they are not recorded
biological motion; mocap can replace this preview later. The complete trial sequence contains 9 maneuvers (straight,
left/right 30°, 60°, 90°, stairs up/down), 3 cutoffs (−500 / −250 / 0 ms), and 2 repetitions across two avatar
appearances and two vision conditions: 216 main trials, 16 catch trials, and 9 practice trials (241 total).

**Try-it-now limitation:** the existing walk animation drives the body while temporary scripted paths create the
turn and stair movements. Biological-motion points are attached to that avatar's bones. These are trial-flow/demo
stimuli, not recorded or FreeMoCap biological motion. The separate recorded-take pilot below is for a later stage;
it is not connected to the active app scene yet.

Simulated-RP perception pilot. The participant stands still in passthrough. Each trial plays one real recorded
take with root motion, and the avatar disappears at a cutoff relative to T0 (−500 / −250 / 0 ms). The participant
then reports where the avatar was heading. When the avatar disappears it is always exactly `cutoffDistance`
straight ahead of the participant, whatever the cutoff.

The recorded-take tooling and trial manager live in `Assets/PerceptionPilot/`. They are for a later mocap workflow.

## 1. Clips → Humanoid with root motion
1. Put the recorded takes in a folder, e.g. `Assets/Mocap/`. Name the files like
   `W1_TurnL60_T03.fbx`, `W1_TurnR30_T01.fbx`, `W1_Straight_T02.fbx`, `W2_StairsUp_T04.fbx`, `W1_StairsDown_T01.fbx`.
   Mixamo names such as "Walking Turn 90 Left" or "Ascending Stairs" are recognised too.
2. Select the files (or the folder) and run **Tools > Perception Pilot > 1. Set Humanoid + root-motion import**.
   This sets each clip to Humanoid, root rotation / Y / XZ *not* baked into pose (based on body orientation and feet), and loop time off.
3. Your character FBX (`casual-f-0170.fbx` or the new short-hair character) must also be **Rig > Humanoid**.
   Open it once and check the bone mapping under **Configure**.

## 2. Library + T0
1. With the clips still selected, run **Tools > Perception Pilot > 2. Build or update library from selected clips**.
   This creates `ManeuverLibrary.asset`, guesses type, walker and take from the names, and computes T0 automatically.
   The Console prints a report. Fix any entry whose type is wrong or whose note says the measured turn doesn't match the label.
2. Run **Tools > Perception Pilot > 5. T0 Inspector** and step through every take. The plot shows the sideways path
   (or height, for stairs). The red line is T0 and the grey lines are the cutoffs. T0 belongs where the path leaves the
   regular straight-walking wave. **If unsure, move it earlier**: a late T0 would let the 0 ms cutoff show part of the real path change.
   Clicking the plot moves T0 and locks it, so a re-analysis won't overwrite it.
   - Automatic T0 is the first sustained departure from the sway-corrected straight path. On synthetic gait it lands
     about 75–220 ms after the mathematical start of the turn, which is the first physically detectable change. Report this definition in the paper.
   - Straight takes get a **pseudo-T0**: the median T0 of the same walker's maneuver takes. This assumes all takes were
     trimmed the same way. Adjust it by hand if needed so straight trials stop at a comparable point in the step cycle.
3. A take needs about 2.9 s of walking before T0 (2 s lead-in + 0.4 s jitter + 0.5 s earliest cutoff). Shorter takes still work,
   but their lead-in gets shortened. This is flagged in the note and logged as `lead_in_s`.

## 2b. Real biological motion (point-light condition)
The point-light condition plays the **recorded walker's own joints** (FreeMoCap 3D output). It does not use dots attached to the avatar's bones.
For every take:
1. In the take's FreeMoCap session, copy `saved_data/csv/body_trajectories.csv`.
2. Put the copy next to the take's FBX with the **same name**: `W1_TurnL60_T03.fbx` + `W1_TurnL60_T03.csv`.
3. Run step 2 or step 3. The library attaches the CSV automatically and measures T0 on the recorded hip path (`t0PointLight`).
   It also compares that with the clip's T0. A difference over 50 ms means the FBX and the CSV don't start on the same frame. Fix the trim before running participants.

Defaults: 13 classic markers (head centre, shoulders, elbows, wrists, hips, knees, ankles). Tick *Include Feet* on `RealPointLight` for 15.
FreeMoCap 3D data is Z-up in millimetres, which is the default (*Up Axis* = ZUp, units auto-detected). The feet are placed on the real floor automatically.
With *Require Real Biological Motion* on (the default), point-light trials use only takes that have a CSV. The trial log column `stim_source` records
`avatar`, `recorded_joints` or `avatar_bones` (the fallback).

Note for the write-up: the realistic avatar shows the same take retargeted onto the character, while the dots show the raw recording.
Retargeting changes the kinematics slightly, so the two conditions share the same recorded movement but are not geometrically identical.

## 3. Scene
1. Use a scene with an **OVRCameraRig**, passthrough, and *Tracking Origin Type = Floor Level*.
2. Drag the avatar into the scene **at the root (no parent)**. Disable everything else that moves it:
   AutoWalkDriver, SimpleWalkDriver, the CharacterController, and any other Animator controller scripts.
3. Select the avatar and run **Tools > Perception Pilot > 4. Create experiment rig**.
   This adds `AvatarStimulus` + `PointLightRig` to the avatar and creates the `PerceptionPilot` object
   (trial manager, controller ray, head-locked prompt). The dialog lists anything still to fix.
4. On `PerceptionPilot` > Perception Trial Manager:
   - **Library**: `ManeuverLibrary.asset`
   - **Rp Overlay**: the project's `LowVisionOverlayController` (found automatically if it's in the scene).
     Full field switches the overlay off. Simulated RP applies *Simulated Rp Vision* (default Glaucoma). The window size comes from the overlay's
     *Tunnel Fov Degrees*: the diameter at the 50% point of a *Tunnel Edge Degrees* soft edge, with a clear interior (*Tunnel Interior Alpha* 0).
   - **Research Logger**: the project's `ResearchSessionLogger` (found automatically). Each trial calls
     `BeginTrial` (trial id, condition = avatar|field|source|maneuver, exemplar = take, cutoff, expected answer), then `RecordResponse` and `EndTrial`.
     The Neon gaze samples, the RP window and the gaze body-region hits are therefore logged per frame, tagged with the trial.
     If the gaze body-region proxies should follow the realistic avatar, point the `AvatarRegionRig` at the same avatar.
   - **On Marker (string)**: optional extra event hook.
   - Optional **Stairs Up / Down Visual**: shown only in practice and catch stairs trials (pivot = edge of the first step, +Z = walking direction).
   - **Participant Id** for each person. It sets the Latin-square order and the random design. Use **Start At Block** to resume after an interruption.

## 4. Run
- The participant stands on a floor mark and looks ahead. Pull the trigger to calibrate the standing point and the straight-ahead direction.
- **Response**: point the ray at the floor where the avatar was heading and pull the trigger
  (|angle| ≤ 15° counts as straight). **A** = stairs up, **B** = stairs down.
- **Editor testing without the headset**: Space = trigger; ← / ↑ / → = left 60° / straight / right 60°; U / D = stairs.
- **Recorded-take pilot design**: 9 maneuvers × 3 cutoffs × 2 repetitions × 4 conditions (Realistic / recorded Point-light × Full / Simulated RP) = 216 trials,
  plus 2 catch trials per sub-block (16) and 9 practice trials. This describes the separate recorded-take manager, not the try-it-now app mode above.
- **Output**: one CSV per session in `Application.persistentDataPath/PerceptionPilot/`.
  On Quest that is `/sdcard/Android/data/<package>/files/PerceptionPilot/`.

## 5. First check (do this before anything else)
Run a few trials and open the CSV. `actual_D_m` should be close to `target_D_m` (within a few cm), and `actual_lateral_m` close to 0.
If the avatar walks in place, or appears at the wrong distance, root motion isn't reaching the avatar.
Check that the clips are Humanoid with root motion (step 1) and that no other script or Animator controller is driving the avatar.
