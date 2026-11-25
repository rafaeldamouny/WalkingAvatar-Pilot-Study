import json, numpy as np, matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
import build_library as L, t0 as T
man=json.load(open(__import__('sys').argv[1]+'/manifest.json')); J_=man['joints']
fig,axes=plt.subplots(len(man['clips']),3,figsize=(13,2.3*len(man['clips'])))
for row,c in zip(axes,man['clips']):
    A=np.fromfile(__import__('sys').argv[1]+f"/{c['file']}.bytes",'<f4').reshape(c['frames'],len(J_),3); t=np.arange(c['frames'])/c['fps']-c['t0']
    r=A[:,0]
    ax=row[0]; ax.plot(r[:,0],r[:,2],'k',lw=1); 
    for cut,col in ((-.5,'C0'),(-.25,'C1'),(0,'C3')):
        i=np.argmin(abs(t-cut)); ax.plot(r[i,0],r[i,2],'o',color=col,ms=4)
    for s in c['leadIn']:
        i=np.argmin(abs(t-s['seamRelT0'])); ax.plot(r[i,0],r[i,2],'x',color='m')
    ax.set_aspect('equal'); ax.set_title(c['id']+'  top view (x right, z fwd)',fontsize=8)
    lr=A[:,J_.index('RightUpLeg')]-A[:,J_.index('LeftUpLeg')]; f=np.cross(lr,[0,1,0]); yaw=np.degrees(np.unwrap(np.arctan2(f[:,0],f[:,2])))
    row[1].plot(t,yaw,'k',lw=.8); row[1].set_title('pelvis yaw (deg, + = right)',fontsize=8)
    row[2].plot(t,A[:,0,1],'k',lw=.8); row[2].plot(t,np.minimum(A[:,J_.index('LeftFoot'),1],A[:,J_.index('RightFoot'),1]),'g',lw=.6); row[2].set_title('hip / lower ankle height (m)',fontsize=8)
    for ax in row[1:]:
        for cut,col in ((-.5,'C0'),(-.25,'C1'),(0,'C3')): ax.axvline(cut,color=col,lw=.8)
        for s in c['leadIn']: ax.axvline(s['seamRelT0'],color='m',ls=':',lw=.8)
        ax.axvline(-c['recordedTargetStartsRelT0']*-1 if False else c['recordedTargetStartsRelT0'],color='gray',ls='--',lw=.8)
        ax.set_xlim(-3,2); ax.tick_params(labelsize=7)
plt.tight_layout(); plt.savefig('verify_clips.png',dpi=70)
