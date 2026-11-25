# Mocap library builder

Builds `Assets/ExperimentIntegration/Resources/Mocap` from the CMU motion capture database.

1. Get the CMU BVH files (cgspeed conversion): `git clone --filter=blob:none --sparse https://github.com/una-dinosauria/cmu-mocap`
   then `git sparse-checkout set data/069 data/114`. Set the path `B` in `build_library.py`.
2. `python run_build.py <output folder>` (numpy). Prints T0, measured turn/rise and splice seams per clip.
3. Checks: `plots.py` (paths, pelvis heading, hip/ankle height with T0, cutoffs and seams), `kink.py` (heading
   drift during the lead-in), `vidcmp.py` (our forward kinematics next to CMU's reference .avi renders).

Clip choice, trims and lead-in donors are listed in `run_build.py`.
Verification run on 2026-11-03: root positions of the BVH match the original AMC files (69_28, 69_31, 16_17, 16_19) to
5e-5 units; the lateral path of 69_31 / 69_28 correlates r = 0.98 with the walker's image position in CMU's videos
(left turn → moves to the image right while walking toward the camera, right turn → image left).
