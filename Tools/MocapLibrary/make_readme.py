import json, sys
D = sys.argv[1]
m = json.load(open(D + '/manifest.json'))
rows = []
for c in m['clips']:
    li = ', '.join(f"{l['donor']} (seam {l['seamRelT0']:+.2f} s)" for l in c['leadIn']) or 'none'
    rows.append(f"| {c['maneuver']} | {c['trial']} | {c['t0']:.2f}{' (pseudo)' if c['t0IsPseudo'] else ''} | {c['measuredTurnDeg']:+.0f}° / {c['measuredRiseM']:+.2f} m | {-c['recordedTargetStartsRelT0']:.2f} s | {li} |")
txt = f"""# Recorded walking clips (CMU motion capture)

{m['attribution']}

CMU's terms: the data may be used for research and commercial products, but the motion data itself may not be resold.
Keep this file with the clips. If results are published, include the acknowledgement above.

**Files per clip.**
- `<id>.bytes`: joint centres of the recorded skeleton, float32, frames × {len(m['joints'])} joints × xyz, 120 Hz, metres,
  Unity axes. Joints: {', '.join(m['joints'])}. Used by the point-light and stick figures.
- `<id>_rot.bytes`: the recorded local joint rotations of the same frames, float32 quaternions (x, y, z, w), frames × the
  subject's {len(m['skeletons'][0]['names'])} CMU skeleton joints (`skeletons` in manifest.json: names, parents, rest
  offsets). Used by the realistic avatar through Unity humanoid retargeting (MocapAvatarDriver).
{m['conversion']}
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
""" + '\n'.join(rows) + """

Turn sign: + = right. Rebuild with `Tools/MocapLibrary/run_build.py` (needs the CMU BVH files; see that folder's README).
"""
open(D + '/README.md', 'w').write(txt)
