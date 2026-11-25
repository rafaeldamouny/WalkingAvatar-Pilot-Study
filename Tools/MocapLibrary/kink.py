import json,numpy as np,sys,t0 as T
man=json.load(open(sys.argv[1]+'/manifest.json')); J_=man['joints']
for c in man['clips']:
    A=np.fromfile(f"{sys.argv[1]}/{c['file']}.bytes",'<f4').reshape(c['frames'],len(J_),3)
    t=np.arange(c['frames'])/120-c['t0']; r=A[:,0,[0,2]]
    v=np.gradient(r,1/120,axis=0); h=np.degrees(np.unwrap(np.arctan2(v[:,0],v[:,1]))); hs=T.smooth(h,1.1*120)
    m=(t>-2.9)&(t<-.3); print(f"{c['id']:22s} path-heading range pre-T0 {np.ptp(hs[m]):5.1f} deg")
