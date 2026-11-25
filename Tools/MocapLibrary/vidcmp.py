import numpy as np, subprocess, matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
import build_library as L
SEG=[(0,1),(1,2),(2,3),(3,4),(4,5),(5,6),(6,7),(7,8),(7,10),(10,11),(11,12),(12,13),(8,13),(13,14),(14,15),(15,16),(8,17),(17,18),(18,19),(19,20)]
# joints: 0 Hips 1 Spine 2 Spine1 3 Neck 4 Neck1 5 Head 6 HeadTop 7 LArm 8 LForeArm 9 LHand 10 RArm 11 RFore 12 RHand 13 LUpLeg 14 LLeg 15 LFoot 16 LToe 17 RUpLeg 18 RLeg 19 RFoot 20 RToe
B=[(0,1),(1,2),(2,3),(3,4),(4,5),(5,6),(3,7),(7,8),(8,9),(3,10),(10,11),(11,12),(0,13),(13,14),(14,15),(15,16),(0,17),(17,18),(18,19),(19,20)]
for f in ['69_31','69_28']:
    J=L.load(f)['J'].copy(); J[...,0]*=-1   # Unity-handed copy of the raw (unspliced) recording
    raw=subprocess.run(['ffmpeg','-v','error','-i',f'/mnt/user-data/uploads/Downloads/{f}.avi','-f','rawvideo','-pix_fmt','rgb24','-'],capture_output=True).stdout
    V=np.frombuffer(raw,np.uint8).reshape(-1,240,320,3)
    # camera in front of the walker, looking back at it (walker approaches the camera)
    p0=J[0,0]; d=J[60,0]-J[0,0]; d[1]=0; d/=np.linalg.norm(d)
    cam=p0+d*7.0+np.array([0,1.0,0]); fwd=-d; right=np.cross([0,1,0],fwd); right/=np.linalg.norm(right)  # left-handed: right = up x fwd
    times=np.arange(0,min(len(V)/30,len(J)/120)-.05,.5)
    fig,ax=plt.subplots(2,len(times),figsize=(2*len(times),4.4))
    for k,tt in enumerate(times):
        ax[0,k].imshow(V[int(tt*30)]); ax[0,k].set_axis_off(); ax[0,k].set_title(f'CMU video {tt:.1f}s',fontsize=7)
        P=J[int(tt*120)]-cam; x=P@right; y=P[:,1]; z=P@fwd
        u=x/z; v=y/z
        for a,b in B: ax[1,k].plot([u[a],u[b]],[v[a],v[b]],'k',lw=1.2)
        ax[1,k].set_xlim(-.45,.45); ax[1,k].set_ylim(-.25,.25); ax[1,k].set_aspect('equal'); ax[1,k].set_axis_off(); ax[1,k].set_title('our FK (Unity axes)',fontsize=7)
    plt.tight_layout(); plt.savefig(f'vidcmp_{f}.png',dpi=80)
