"""Build the pilot's recorded-motion library from CMU mocap (BVH conversions of the CMU ASF/AMC files).

Output: Assets/ExperimentIntegration/Resources/Mocap/
  manifest.json        clip list, T0, provenance, measured turn/rise
  <id>.bytes           float32 little-endian, frames x joints x 3 (metres, Unity left-handed Y-up)
Every clip is put in a canonical frame: the walking direction before T0 is +Z, the floor (start level) is y=0.
"""
import json, os, sys, struct
import numpy as np
import bvh, t0 as T0
from scipy.spatial.transform import Rotation as Rot, Slerp

B = '/home/claude/una-dinosauria/cmu-mocap/data/'
OUT = sys.argv[1] if len(sys.argv) > 1 else '/home/claude/mocapwork/out'
FPS = 120.0
JOINTS = ['Hips', 'Spine', 'Spine1', 'Neck', 'Neck1', 'Head', 'HeadTop',
          'LeftArm', 'LeftForeArm', 'LeftHand', 'RightArm', 'RightForeArm', 'RightHand',
          'LeftUpLeg', 'LeftLeg', 'LeftFoot', 'LeftToeBase', 'RightUpLeg', 'RightLeg', 'RightFoot', 'RightToeBase',
          'LeftHandIndex1', 'RightHandIndex1']
PRE_T0 = 3.0     # seconds of walking needed before T0 (2.0 lead-in + 0.4 jitter + 0.5 earliest cutoff + margin)
POST_T0 = 1.6    # catch trials show 1.5 s after T0
ALIGN = 'vel'
BLEND = 0.2      # cross-fade at a splice seam
SEAM_GAP = 1.0   # seam must be at least this long before T0 (earliest cutoff is 0.5 s before T0)

_cache = {}
def load(name):
    if name not in _cache:
        sub = name.split('_')[0]
        d = bvh.load(f'{B}{int(sub):03d}/{name}.bvh')
        P = d['pos']; n = d['names']
        cols = []
        for j in JOINTS:
            cols.append(d['ends']['Head'] if j == 'HeadTop' else P[:, n.index(j)])
        d['J'] = np.stack(cols, 1)           # F, 21, 3 right-handed Y-up metres
        F_, K_ = d['local'].shape[:2]
        d['Q'] = Rot.from_matrix(d['local'].reshape(-1, 3, 3)).as_quat().reshape(F_, K_, 4)   # local rotations, xyzw
        _cache[name] = d
    return _cache[name]

def yaw_of(J):
    lr = J[:, JOINTS.index('LeftUpLeg')] - J[:, JOINTS.index('RightUpLeg')]
    f = np.cross(lr, [0, 1, 0])
    return np.unwrap(np.arctan2(f[:, 0], f[:, 2]))

def rot_y(J, ang):
    c, s = np.cos(ang), np.sin(ang)
    out = J.copy()
    x, z = J[..., 0], J[..., 2]
    out[..., 0] = c * x + s * z
    out[..., 2] = -s * x + c * z
    return out          # rotates heading by +ang (heading = atan2(x, z))

def speed(J):
    r = J[:, 0, [0, 2]]
    return T0.smooth(np.linalg.norm(np.gradient(r, 1 / FPS, axis=0), axis=1), .25 * FPS)

def floor_of(J, a, b):
    feet = J[a:b][:, [JOINTS.index(k) for k in ('LeftToeBase', 'RightToeBase', 'LeftFoot', 'RightFoot')], 1]
    return np.percentile(feet.min(1), 2)

def steady(J, a, b, vmin=.7):
    """Indices in [a,b) of steady, straight, level walking."""
    sp = speed(J)
    yaw = T0.smooth(np.degrees(yaw_of(J)), 1.1 * FPS)
    hip = T0.smooth(J[:, 0, 1], .55 * FPS)
    ok = (sp > vmin)
    idx = np.nonzero(ok[a:b])[0] + a
    return idx, yaw, hip

def pose_feat(J, i):
    """Root-relative, heading-normalised pose + velocity at frame i."""
    y = yaw_of(J[max(0, i - 3):i + 4])[3 if i >= 3 else i]
    P = rot_y(J[i] - J[i, 0] * [1, 0, 1], -y)
    V = rot_y((J[min(len(J) - 1, i + 2)] - J[max(0, i - 2)]) * FPS / 4, -y)
    return np.concatenate([P.ravel(), .1 * V.ravel()])

def splice(D, d_rng, C, c_rng):
    """Join donor D (frames d_rng) before clip C (frames c_rng) at the best-matching pair, aligned and cross-faded.
    Returns (joined array, offset of C frame 0 in the joined array, info)."""
    h = int(BLEND * FPS / 2)
    best = None
    for d in range(d_rng[0] + h, d_rng[1] - h, 2):
        fd = pose_feat(D, d)
        for c in range(c_rng[0] + h, c_rng[1] - h, 2):
            e = np.sum((fd - pose_feat(C, c)) ** 2)
            if best is None or e < best[0]: best = (e, d, c)
    e, d, c = best
    # align C to D at the seam: horizontal root position and pelvis heading; vertical floor-to-floor
    D = D + np.array([0, C[c, 0, 1] - D[d, 0, 1], 0])   # level the donor with the clip at the seam (same walker)
    if ALIGN == 'vel':
        vd = D[d + h, 0, [0, 2]] - D[d - h, 0, [0, 2]]; vc = C[c + h, 0, [0, 2]] - C[c - h, 0, [0, 2]]
        yd = np.arctan2(vd[0], vd[1]); yc = np.arctan2(vc[0], vc[1])
    elif ALIGN == 'path':
        k = int(.55 * FPS)
        pd = D[d, 0, [0, 2]] - D[max(0, d - k), 0, [0, 2]]; pc = C[min(len(C) - 1, c + k), 0, [0, 2]] - C[c, 0, [0, 2]]
        yd = np.arctan2(pd[0], pd[1]); yc = np.arctan2(pc[0], pc[1])
    else:
        yd = yaw_of(D)[d]; yc = yaw_of(C)[c]
    Ca = rot_y(C - C[c, 0] * [1, 0, 1], yd - yc) + D[d, 0] * [1, 0, 1]
    w = (np.arange(-h, h + 1) + h) / (2 * h)
    w = w * w * (3 - 2 * w)
    blend = (1 - w)[:, None, None] * D[d - h:d + h + 1] + w[:, None, None] * Ca[c - h:c + h + 1]
    out = np.concatenate([D[:d - h], blend, Ca[c + h + 1:]], 0)
    rms = np.sqrt(np.mean((D[d] - Ca[c]) ** 2))
    return out, (d - h) - (c - h), dict(seam_rms_m=float(rms), d=d, c=c, ang=float(yd - yc), h=h)

def splice_q(DQ, CQ, d, c, ang, h):
    """Apply the same join to local joint rotations: the clip's root is turned by the alignment angle, then
    every joint is slerped across the cross-fade window."""
    CQ = CQ.copy()
    CQ[:, 0] = (Rot.from_euler('y', ang) * Rot.from_quat(CQ[:, 0])).as_quat()
    w = (np.arange(-h, h + 1) + h) / (2 * h); w = w * w * (3 - 2 * w)
    a = DQ[d - h:d + h + 1]; b = CQ[c - h:c + h + 1]
    blend = np.empty_like(a)
    for k in range(a.shape[1]):
        for i in range(len(w)):
            r = Slerp([0, 1], Rot.from_quat([a[i, k], b[i, k]]))(w[i])
            blend[i, k] = r.as_quat()
    return np.concatenate([DQ[:d - h], blend, CQ[c + h + 1:]], 0)

def canonical(J, t0i, floor_y):
    a = max(0, t0i - int(1.5 * FPS)); b = max(a + 10, t0i - int(.2 * FPS))
    path = J[b, 0, [0, 2]] - J[a, 0, [0, 2]]
    head = np.arctan2(path[0], path[1])
    out = rot_y(J - J[t0i, 0] * [1, 0, 1], -head)
    out[..., 1] -= floor_y
    out[..., 0] *= -1        # right-handed -> Unity left-handed (body left stays left)
    return out

def canonical_q(Q, J, t0i):
    a = max(0, t0i - int(1.5 * FPS)); b = max(a + 10, t0i - int(.2 * FPS))
    path = J[b, 0, [0, 2]] - J[a, 0, [0, 2]]
    head = np.arctan2(path[0], path[1])
    Q = Q.copy()
    Q[:, 0] = (Rot.from_euler('y', -head) * Rot.from_quat(Q[:, 0])).as_quat()
    Q[..., 1] *= -1; Q[..., 2] *= -1     # mirror x (right- to left-handed): (x, y, z, w) -> (x, -y, -z, w)
    return Q

def measure(Ju, t0i):
    """Unity-space measurements: signed turn (deg, + = right), lateral at end, rise."""
    yaw = np.degrees(np.unwrap(np.arctan2(*(np.cross(Ju[:, JOINTS.index('RightUpLeg')] - Ju[:, JOINTS.index('LeftUpLeg')], [0, 1, 0])[:, [0, 2]].T))))
    # left-handed: cross(R-L, up) points forward
    pre = yaw[max(0, t0i - int(1.0 * FPS)):t0i].mean()
    post = yaw[-int(.4 * FPS):].mean()
    rise = T0.smooth(Ju[:, 0, 1], .55 * FPS)
    return dict(turn_deg=float(post - pre), rise_m=float(rise[-int(.3 * FPS):].mean() - rise[max(0, t0i - int(.6 * FPS)):t0i].mean()))

def pre_t0_walk(J, t0i):
    """Seconds of continuous walking (speed > .6 m/s) before T0."""
    sp = speed(J)
    i = t0i
    while i > 0 and sp[i - 1] > .6: i -= 1
    return (t0i - i) / FPS, i

def steady_range(name, vmin=.75, tmin=None, tmax=None):
    J = load(name)['J']; sp = speed(J); F = len(J)
    a = int((tmin or 0) * FPS); b = int((tmax * FPS) if tmax else F)
    ok = sp[a:b] > vmin
    # longest run
    best = (0, 0); i = 0
    while i < len(ok):
        if ok[i]:
            j = i
            while j < len(ok) and ok[j]: j += 1
            if j - i > best[1] - best[0]: best = (i, j)
            i = j
        else: i += 1
    return a + best[0], a + best[1]

def build_with_leadin(target, c_window, donors, t0_fn, need_pre=PRE_T0):
    """Prepend donor walking (same subject) until there is need_pre seconds of walking before T0."""
    C = load(target)['J']
    t0i = t0_fn(C)
    joined = C; offset = 0; seams = []
    pre, start = pre_t0_walk(joined, t0i)
    di = 0
    cw = c_window
    while pre < need_pre and di < len(donors):
        dname, (da, db) = donors[di]; di += 1
        D = load(dname)['J'][da:db]
        out, off, info = splice(D, (max(0, len(D) - int(1.25 * FPS)), len(D)), joined, cw)
        joined = out; offset += off; t0i += off
        seams.append(dict(donor=dname, seam_s_before_t0=None, **info, seam_frame=None))
        seams[-1]['seam_frame'] = off + cw[0]  # approx; recomputed below
        pre, start = pre_t0_walk(joined, t0i)
        cw = (0, int(.6 * FPS))   # next donor joins at the start of the current joined clip
    return joined, t0i, seams, pre

def detect_t0(J, kind, guess_i, ref_len=1.1, gap=.8):
    """Onset of the maneuver: first sustained departure from the straight, level walking that precedes it.
    Signals are averaged over one stride (turns) or one step (stairs) so gait oscillation cancels; centred
    averaging can only move the onset earlier, which is the safe direction for occlusion cutoffs."""
    b = max(10, guess_i - int(gap * FPS)); a = max(0, b - int(ref_len * FPS))
    if kind == 'turn':
        yaw = T0.smooth(np.degrees(yaw_of(J)), 1.1 * FPS)
        dev = yaw - yaw[a:b].mean(); thr = 6.0
        # lateral path deviation from the reference walking line
        r = J[:, 0, [0, 2]]; d = r[b] - r[a]; d /= np.linalg.norm(d)
        lat = T0.smooth((r - r[a]) @ np.array([d[1], -d[0]]), 1.1 * FPS)
        devl = lat - lat[a:b].mean(); thrl = .04
        sg = np.sign(dev[-1])
        on = []
        for s, th in ((dev, thr), (devl * np.sign(devl[-1]) * sg, thrl)):
            idx = np.nonzero(sg * s[b:] > th)[0]
            if len(idx):
                i = b + idx[0]
                while i > b and sg * s[i] > th * .25: i -= 1
                on.append(i)
        return min(on)
    else:
        hip = T0.smooth(J[:, 0, 1], .55 * FPS)
        dev = hip - hip[a:b].mean(); sg = np.sign(dev[-1] if abs(dev[-1]) > .05 else dev[np.argmax(abs(dev))])
        idx = np.nonzero(sg * dev[b:] > .03)[0]
        i = b + idx[0]
        while i > b and sg * dev[i] > .0075: i -= 1
        return i

def raw_guess(J, kind, start=0):
    if kind == 'turn':
        yaw = T0.smooth(np.degrees(yaw_of(J)), .55 * FPS)
        base = yaw[start:start + int(.3 * FPS)].mean()
        return start + int(np.argmax(abs(yaw[start:] - base) > 12))
    hip = T0.smooth(J[:, 0, 1], .55 * FPS)
    base = np.median(hip[start:start + int(.6 * FPS)])
    return start + int(np.argmax(abs(hip[start:] - base) > .05))

def make_clip(target, kind, donors, c_window_s=(0, .45), start_s=0.0, end_s=None, pseudo_t0=False):
    """kind: 'turn' | 'stairs' | 'straight'. Donor walking from the same subject is prepended (phase-matched,
    cross-faded) until there are PRE_T0 seconds before T0. Seams are kept before the recorded maneuver approach."""
    full = load(target)['J']
    a = int(start_s * FPS); b = int(end_s * FPS) if end_s else len(full)
    C = full[a:b]; CQ = load(target)['Q'][a:b]
    g = None if kind == 'straight' else raw_guess(C, kind)
    cw_end = int(c_window_s[1] * FPS)
    if g is not None: cw_end = max(int(BLEND * FPS) + 8, min(cw_end, g - int(.5 * FPS)))
    cw = (int(c_window_s[0] * FPS), cw_end)
    joined = C; seams = []; off_total = 0
    need = PRE_T0 + (1.6 if kind == 'straight' else 0)   # straight: room for a pseudo-T0 plus a full catch trial
    have = lambda: (g + off_total if g is not None else len(joined) - int(1.6 * FPS)) / FPS
    for dname, (da, db) in donors:
        if (len(joined) / FPS if kind == 'straight' else have()) >= need + .2: break
        D = load(dname)['J'][da:db]
        out, off, info = splice(D, (max(0, len(D) - int(1.25 * FPS)), len(D)), joined, cw)
        CQ = splice_q(load(dname)['Q'][da:db], CQ, info['d'], info.pop('c'), info.pop('ang'), info.pop('h'))
        for sm in seams: sm['frame'] += off
        seams.append(dict(donor=dname, donor_frames=[int(da), int(db)], frame=int(info.pop('d')), **info))
        joined = out; off_total += off
        cw = (0, int(.6 * FPS))
    if kind == 'straight':
        t0i = off_total + int(.55 * FPS)        # pseudo-T0 0.55 s into the target recording; seams sit before it, as in turn clips
    else:
        t0i = detect_t0(joined, kind, g + off_total)
    floor = floor_of(joined, max(0, t0i - int(2 * FPS)), t0i)
    U = canonical(joined, t0i, floor)
    UQ = canonical_q(CQ, joined, t0i)
    assert len(UQ) == len(U)
    m = measure(U, t0i)
    for sm in seams: sm['seam_s_rel_t0'] = round((sm.pop('frame') - t0i) / FPS, 3)
    recorded_from = (off_total - t0i) / FPS     # where the target recording itself starts, relative to T0
    return dict(J=U, Q=UQ, t0i=t0i, seams=seams, pre=t0i / FPS, post=(len(U) - t0i) / FPS, **m,
                t0_in_source_s=round((t0i - off_total) / FPS + start_s, 3), target_starts_rel_t0=round(recorded_from, 3))

S69 = ['69_02', '69_04', '69_03', '69_05', '69_01']
def donors69(exclude=()):
    return [(n, steady_range(n)) for n in S69 if n not in exclude]

def _gait_fit(t, y, a, b, harmonics=2):
    """Least-squares fit of trend + periodic gait component on [a,b); returns prediction for all t and ref residual sd."""
    best = None
    for f in np.linspace(.7, 1.15, 46):
        cols = [np.ones_like(t), t - t[a]]
        for k in range(1, harmonics + 1):
            cols += [np.sin(2 * np.pi * k * f * t), np.cos(2 * np.pi * k * f * t)]
        X = np.stack(cols, 1)
        coef, *_ = np.linalg.lstsq(X[a:b], y[a:b], rcond=None)
        r = y[a:b] - X[a:b] @ coef
        sse = float(r @ r)
        if best is None or sse < best[0]: best = (sse, X @ coef, np.sqrt(sse / (b - a)), f)
    return best[1], best[2], best[3]

def detect_t0(J, kind, guess_i, ref_len=2.2):
    """T0 = first sustained departure from the straight/level walking that precedes the maneuver.
    The walking before the guess is modelled as trend + stride-periodic sway (2 harmonics); T0 is where the
    residual leaves the band and keeps growing, back-tracked to where it first left 1.5 x the reference sd."""
    t = np.arange(len(J)) / FPS
    b = max(int(.8 * FPS), guess_i - int(.15 * FPS)); a = max(0, b - int(ref_len * FPS))
    sigs = []
    if kind == 'turn':
        sigs.append((T0.smooth(np.degrees(yaw_of(J)), .05 * FPS), 5.0))
        r = J[:, 0, [0, 2]]; d = r[b] - r[a]; d = d / np.linalg.norm(d)
        sigs.append((T0.smooth((r - r[a]) @ np.array([d[1], -d[0]]), .05 * FPS), .03))
    else:
        sigs.append((T0.smooth(J[:, 0, 1], .05 * FPS), .03))
        foot = np.maximum(J[:, JOINTS.index('LeftFoot'), 1], J[:, JOINTS.index('RightFoot'), 1])
        sigs.append((T0.smooth(foot, .05 * FPS), .05))
    onsets = []
    for y, thr in sigs:
        pred, sd, f = _gait_fit(t, y, a, b)
        res = y - pred
        hold = int(.2 * FPS)
        # direction: sign of the residual at the end of the maneuver window
        end = min(len(J) - 1, guess_i + int(1.2 * FPS))
        sg = np.sign(res[end]) or 1
        lim = max(thr, 4 * sd)
        found = None
        for i in range(b, len(J) - hold):
            if np.all(sg * res[i:i + hold] > lim):
                found = i; break
        if found is None: continue
        i = found
        while i > a and sg * res[i] > 1.5 * sd: i -= 1
        onsets.append(i)
    return min(onsets)

def straight_windows(name, vmin, length=2.4, max_yaw=8.0, start=0, end=None):
    """Non-overlapping windows of steady, straight, level walking (candidate lead-in donors)."""
    J = load(name)['J']; sp = speed(J)
    yaw = T0.smooth(np.degrees(yaw_of(J)), 1.1 * FPS); hip = T0.smooth(J[:, 0, 1], .55 * FPS)
    L = int(length * FPS); out = []; i = int(start * FPS); e = int(end * FPS) if end else len(J)
    while i + L <= e:
        w = slice(i, i + L)
        if sp[w].min() > vmin and np.ptp(yaw[w]) < max_yaw and np.ptp(hip[w]) < .03:
            out.append((name, (i, i + L))); i += L
        else: i += int(.1 * FPS)
    return out
