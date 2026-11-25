import bvh, numpy as np
def smooth(x,n):
    n=max(1,int(n)); k=np.ones(n)/n
    return np.convolve(np.pad(x,(n//2,n-1-n//2),mode='edge'),k,'valid')
def yaw_series(d):
    n=d['names']; p=d['pos']
    l=p[:,n.index('LeftUpLeg')]; r=p[:,n.index('RightUpLeg')]; f=np.cross(l-r,[0,1,0])
    return np.degrees(np.unwrap(np.arctan2(f[:,0],f[:,2])))
def turn_t0(d, ref=(None,None), thr_yaw=8.0, thr_lat=0.05):
    """Returns (t0, info). Reference straight-walking window from steady walking start."""
    dt=d['dt']; p=d['pos'][:,0]; t=np.arange(d['F'])*dt
    sp=smooth(np.linalg.norm(np.gradient(p[:,[0,2]],dt,axis=0),axis=1),.25/dt)
    s0=int(np.argmax(sp>0.6))
    stride=1.1
    yaw=yaw_series(d); yaw_s=smooth(yaw,stride/dt)   # stride-averaged pelvis yaw removes gait oscillation
    # reference: mean yaw over first stride of steady walking
    a,b=ref
    if a is None: a=s0
    if b is None: b=min(len(t)-1,a+int(stride/dt))
    yref=yaw[a:b].mean()
    dy=yaw_s-yref
    # path lateral relative to ref heading
    h=np.radians(yref); fwd=np.array([np.sin(h),np.cos(h)]); left=np.array([fwd[1],-fwd[0]])
    rel=p[:,[0,2]]-p[a,[0,2]]; lat=smooth(rel@left,stride/dt)
    lat0=lat[a:b].mean()
    sgn=np.sign(dy[-1])
    def onset(sig,thr):
        idx=np.nonzero(sgn*sig>thr)[0]; idx=idx[idx>b//2] if len(idx) else idx
        if not len(idx): return None
        i=idx[0]
        # back-track to where the signal left 25% of the threshold
        j=i
        while j>a and sgn*sig[j]>thr*0.25: j-=1
        return j
    iy=onset(dy,thr_yaw); il=onset(lat-lat0,thr_lat)
    cands=[i for i in (iy,il) if i is not None]
    i0=min(cands)
    return t[i0], dict(steady_start=t[s0], yaw_onset=t[iy] if iy is not None else None, lat_onset=t[il] if il is not None else None, total_turn=dy[-int(.3/dt)], ref_end=t[b])
def stairs_t0(d, seg=(0,None), thr=0.03):
    dt=d['dt']; n=d['names']; P=d['pos']; t=np.arange(d['F'])*dt
    a,bb=seg; bb=bb or d['F']
    hip=P[:,0,1]; hip_s=smooth(hip,0.55/dt)  # one step
    base=np.median(hip_s[a:a+int(.6/dt)])
    dev=hip_s-base
    direction=np.sign(dev[bb-1]) if abs(dev[bb-1])>0.05 else 1
    idx=np.nonzero(direction*dev[a:bb]>thr)[0]
    i=a+idx[0]; j=i
    while j>a and direction*dev[j]>thr*0.25: j-=1
    return t[j], dict(base=base, direction=direction)
