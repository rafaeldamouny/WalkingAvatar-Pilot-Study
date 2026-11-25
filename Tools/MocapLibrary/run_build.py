import json, os, sys, numpy as np
import build_library as L
OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
d69 = lambda ex=(): [w for n in ['69_02', '69_04', '69_03', '69_05', '69_01'] if n not in ex for w in L.straight_windows(n, .75, 2.0)]
d114 = L.straight_windows('114_13', .6, 2.4)
CMU_DESC = {
 '69_01': 'walk forward', '69_02': 'walk forward', '69_03': 'walk forward', '69_04': 'walk forward', '69_05': 'walk forward',
 '69_28': 'walk forward 90 degree smooth right turn', '69_29': 'walk forward 90 degree smooth right turn', '69_30': 'walk forward 90 degree smooth right turn',
 '69_31': 'walk forward 90 degree smooth left turn', '69_32': 'walk forward 90 degree smooth left turn', '69_33': 'walk forward 90 degree smooth left turn',
 '114_07': 'Walking up and down stairs', '114_08': 'Walking up and down stairs', '114_09': 'Walking up and down stairs', '114_13': 'Walk'}
plan = []
for n in ['69_01', '69_02', '69_03', '69_04', '69_05']:
    w = L.straight_windows(n, .7, 2.2, max_yaw=10)[0][1]
    plan.append(('Straight', n, 'straight', d69((n,)), dict(start_s=w[0] / L.FPS, end_s=w[1] / L.FPS, c_window_s=(0, .3))))
for n in ['69_31', '69_32', '69_33']: plan.append(('TurnLeft90', n, 'turn', d69(), {}))
for n in ['69_28', '69_29', '69_30']: plan.append(('TurnRight90', n, 'turn', d69(), {}))
plan += [('StairsUp', '114_07', 'stairs', d114, dict(start_s=.6, end_s=5.6, c_window_s=(0, .6))),
         ('StairsUp', '114_08', 'stairs', d114, dict(end_s=5.2, c_window_s=(0, .6))),
         ('StairsUp', '114_09', 'stairs', d114, dict(start_s=2.6, end_s=6.5, c_window_s=(0, .6))),
         ('StairsDown', '114_07', 'stairs', d114, dict(start_s=7.3, end_s=12.3, c_window_s=(0, .6))),
         ('StairsDown', '114_08', 'stairs', d114, dict(start_s=6.9, end_s=12.0, c_window_s=(0, .6)))]
clips = []
skeletons = {}
from scipy.spatial.transform import Rotation as Rot
def fk_check(J, Q, sk):
    names = sk['names']; par = sk['parents']; off = np.array(sk['offsets']).reshape(-1, 3)
    F = len(Q); G = [None] * len(names); P = [None] * len(names)
    for j in range(len(names)):
        R = Rot.from_quat(Q[:, j])
        if par[j] < 0: G[j] = R; P[j] = J[:, 0]
        else: G[j] = G[par[j]] * R; P[j] = P[par[j]] + G[par[j]].apply(np.tile(off[j], (F, 1)))
    m = {'Hips': 'Hips', 'LeftUpLeg': 'LeftUpLeg', 'LeftLeg': 'LeftLeg', 'LeftFoot': 'LeftFoot', 'LeftToeBase': 'LeftToeBase',
         'RightLeg': 'RightLeg', 'RightFoot': 'RightFoot', 'Head': 'Head', 'LeftArm': 'LeftArm', 'LeftForeArm': 'LeftForeArm',
         'LeftHand': 'LeftHand', 'RightHand': 'RightHand'}
    return max(np.abs(P[names.index(k)] - J[:, L.JOINTS.index(v)]).max() for k, v in m.items())
for maneuver, src, kind, donors, kw in plan:
    r = L.make_clip(src, kind, donors, **kw)
    cid = f'{maneuver}_{src}'
    J = r['J'].astype('<f4')
    with open(os.path.join(OUT, cid + '.bytes'), 'wb') as f: f.write(J.tobytes())
    Q = r['Q'].astype('<f4')
    with open(os.path.join(OUT, cid + '_rot.bytes'), 'wb') as f: f.write(Q.tobytes())
    sk = src.split('_')[0]
    if sk not in skeletons:
        d0 = L.load(src)
        off = d0['offsets'].copy(); off[:, 0] *= -1
        skeletons[sk] = dict(id=sk, names=d0['names'], parents=d0['parents'], offsets=[round(float(v), 6) for v in off.ravel()])
    # check: forward kinematics of the exported rotations reproduces the exported joint positions
    err = fk_check(r['J'], r['Q'], skeletons[sk])
    print(f'   FK check {cid}: max joint error {err*1000:.1f} mm')
    rise = r['rise_m'] if kind == 'stairs' else 0.0
    clips.append(dict(
        id=cid, maneuver=maneuver, file=cid, frames=int(len(J)), fps=L.FPS, t0=round(r['t0i'] / L.FPS, 4),
        t0IsPseudo=kind == 'straight', startLevel=round(max(0.0, -rise), 3),
        measuredTurnDeg=round(r['turn_deg'], 1), measuredRiseM=round(rise, 3),
        source=f'CMU Graphics Lab Motion Capture Database, subject {src.split("_")[0]}, trial {src} ("{CMU_DESC[src]}")',
        subject=int(src.split('_')[0]), trial=src, sourceSegmentS=[round(kw.get('start_s', 0.0), 3), round(kw.get('end_s') or -1, 3)],
        t0InSourceS=r['t0_in_source_s'], skeleton=src.split('_')[0], recordedTargetStartsRelT0=r['target_starts_rel_t0'],
        leadIn=[dict(donor=s['donor'], donorDescription=CMU_DESC[s['donor']], seamRelT0=s['seam_s_rel_t0'], seamPoseRmsM=round(s['seam_rms_m'], 4)) for s in r['seams']],
        notes=''))
    print(cid, f"T0 {r['t0i']/L.FPS:.2f}s post {r['post']:.2f}s turn {r['turn_deg']:.0f} rise {rise:.2f} recorded approach {-r['target_starts_rel_t0']:.2f}s seams {[s['seam_s_rel_t0'] for s in r['seams']]}")
man = dict(
    joints=L.JOINTS, units='metres', axes='Unity (left-handed, Y up); +Z = walking direction before T0; y=0 = ground level at T0',
    format='float32 little-endian, frames x joints x 3',
    attribution='The data used in this project was obtained from mocap.cs.cmu.edu. The database was created with funding from NSF EIA-0196217.',
    conversion='CMU ASF/AMC converted to BVH (cgspeed conversion, via github.com/una-dinosauria/cmu-mocap); joint positions by forward kinematics of the recorded skeleton. Root positions checked against the original AMC files (max difference 5e-5 units).',
    clips=clips, skeletons=list(skeletons.values()))
json.dump(man, open(os.path.join(OUT, 'manifest.json'), 'w'), indent=1)
