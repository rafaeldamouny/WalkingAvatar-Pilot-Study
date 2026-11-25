# Recorded walking clips (CMU motion capture)

The data used in this project was obtained from mocap.cs.cmu.edu. The database was created with funding from NSF EIA-0196217.

CMU's terms: the data may be used for research and commercial products, but the motion data itself may not be resold.
Keep this file with the clips. If results are published, include the acknowledgement above.

**Files per clip.**
- `<id>.bytes`: joint centres of the recorded skeleton, float32, frames × 23 joints × xyz, 120 Hz, metres,
  Unity axes. Joints: Hips, Spine, Spine1, Neck, Neck1, Head, HeadTop, LeftArm, LeftForeArm, LeftHand, RightArm, RightForeArm, RightHand, LeftUpLeg, LeftLeg, LeftFoot, LeftToeBase, RightUpLeg, RightLeg, RightFoot, RightToeBase, LeftHandIndex1, RightHandIndex1. Used by the point-light and stick figures.
- `<id>_rot.bytes`: the recorded local joint rotations of the same frames, float32 quaternions (x, y, z, w), frames × the
  subject's 31 CMU skeleton joints (`skeletons` in manifest.json: names, parents, rest
  offsets). Used by the realistic avatar through Unity humanoid retargeting (MocapAvatarDriver).
CMU ASF/AMC converted to BVH (cgspeed conversion, via github.com/una-dinosauria/cmu-mocap); joint positions by forward kinematics of the recorded skeleton. Root positions checked against the original AMC files (max difference 5e-5 units).
Forward kinematics of the exported rotations reproduces the exported joint centres within 3 mm (within 17 mm inside the
0.2 s splice cross-fades). The points are skeleton joint centres, **not raw optical-marker trajectories**.

**Canonical frame.** The walking direction before T0 is +Z, the root (Hips) is at the origin at T0, y = 0 is the ground
before T0. `startLevel` raises stairs-down clips so they end on the real floor.

**T0.** First sustained departure from the straight, level walking before the maneuver: pelvis heading and path for
turns; hip and swing-foot height for stairs. The preceding walking is modelled as trend + stride-periodic sway; T0 is
back-tracked to where the residual first left its band. Straight clips get a pseudo-T0 0.55 s into their own recording.

**Lead-in splicing.** The CMU turn and stair clips start less than a second before the maneuver. To give every trial
the app's 2.0 s lead-in (+0.4 s jitter, −0.5 s earliest cutoff), steady straight walking from the **same subject** is
prepended: joined at the best-matching gait phase (pose + velocity), aligned on the walking direction at the seam, and
cross-faded over 0.2 s (positions linearly, rotations by slerp). "Recorded approach" is how much of the target
recording itself precedes T0; earlier walking comes from the donor clips.

| Maneuver | CMU trial | T0 (s) | Measured turn / rise | Recorded approach | Lead-in donors |
|---|---|---|---|---|---|
| Straight | 69_01 | 4.12 (pseudo) | -6° / +0.00 m | 0.55 s | 69_02 (seam -0.42 s), 69_04 (seam -1.17 s), 69_03 (seam -2.42 s) |
| Straight | 69_02 | 4.38 (pseudo) | -6° / +0.00 m | 0.55 s | 69_04 (seam -0.37 s), 69_03 (seam -1.33 s), 69_05 (seam -2.72 s) |
| Straight | 69_03 | 4.35 (pseudo) | -11° / +0.00 m | 0.55 s | 69_02 (seam -0.37 s), 69_04 (seam -1.25 s), 69_05 (seam -2.50 s) |
| Straight | 69_04 | 4.32 (pseudo) | -4° / +0.00 m | 0.55 s | 69_02 (seam -0.37 s), 69_03 (seam -1.33 s), 69_05 (seam -2.63 s) |
| Straight | 69_05 | 4.12 (pseudo) | -6° / +0.00 m | 0.55 s | 69_02 (seam -0.37 s), 69_04 (seam -1.17 s), 69_03 (seam -2.43 s) |
| TurnLeft90 | 69_31 | 3.53 | -90° / +0.00 m | 0.37 s | 69_02 (seam -0.22 s), 69_04 (seam -0.58 s), 69_03 (seam -1.85 s) |
| TurnLeft90 | 69_32 | 3.68 | -98° / +0.00 m | 0.53 s | 69_02 (seam -0.37 s), 69_04 (seam -0.73 s), 69_03 (seam -1.98 s) |
| TurnLeft90 | 69_33 | 3.80 | -95° / +0.00 m | 0.62 s | 69_02 (seam -0.43 s), 69_04 (seam -0.85 s), 69_03 (seam -2.10 s) |
| TurnRight90 | 69_28 | 3.48 | +98° / +0.00 m | 0.97 s | 69_02 (seam -0.75 s), 69_04 (seam -1.78 s) |
| TurnRight90 | 69_29 | 3.21 | +93° / +0.00 m | 0.31 s | 69_02 (seam -0.17 s), 69_04 (seam -1.51 s) |
| TurnRight90 | 69_30 | 3.73 | +90° / +0.00 m | 0.56 s | 69_02 (seam -0.41 s), 69_04 (seam -0.78 s), 69_03 (seam -2.02 s) |
| StairsUp | 114_07 | 4.34 | -1° / +0.32 m | 2.73 s | 114_13 (seam -2.24 s) |
| StairsUp | 114_08 | 4.00 | -7° / +0.39 m | 2.07 s | 114_13 (seam -1.82 s) |
| StairsUp | 114_09 | 3.42 | +3° / +0.40 m | 1.88 s | 114_13 (seam -1.40 s) |
| StairsDown | 114_07 | 4.03 | +2° / -0.44 m | 0.62 s | 114_13 (seam -0.41 s), 114_13 (seam -1.94 s) |
| StairsDown | 114_08 | 4.18 | +2° / -0.44 m | 0.30 s | 114_13 (seam -0.20 s), 114_13 (seam -2.10 s) |

Turn sign: + = right. Rebuild with `Tools/MocapLibrary/run_build.py` (needs the CMU BVH files; see that folder's README).
