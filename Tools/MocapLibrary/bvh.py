import numpy as np, re
UNIT = (1.0/0.45)*0.0254  # CMU ASF length unit -> metres
def rotm(axis, deg):
    a=np.radians(deg); c,s=np.cos(a),np.sin(a)
    n=len(a)
    R=np.zeros((n,3,3)); 
    if axis=='X': R[:,0,0]=1; R[:,1,1]=c; R[:,1,2]=-s; R[:,2,1]=s; R[:,2,2]=c
    if axis=='Y': R[:,1,1]=1; R[:,0,0]=c; R[:,0,2]=s; R[:,2,0]=-s; R[:,2,2]=c
    if axis=='Z': R[:,2,2]=1; R[:,0,0]=c; R[:,0,1]=-s; R[:,1,0]=s; R[:,1,1]=c
    return R
def load(path, drop_first=True):
    txt=open(path).read()
    head,mot=txt.split('MOTION')
    toks=head.split()
    joints=[]; stack=[]; i=0; chans=[]
    while i<len(toks):
        t=toks[i]
        if t in('ROOT','JOINT'):
            joints.append(dict(name=toks[i+1],parent=stack[-1] if stack else -1,offset=None,ch=[],end=None)); cur=len(joints)-1; i+=2
        elif t=='End':
            end=True; i+=2
            # parse '{ OFFSET x y z }'
            assert toks[i]=='{'; off=list(map(float,toks[i+2:i+5])); joints[stack[-1]]['end']=np.array(off); i+=6
        elif t=='{': stack.append(cur); i+=1
        elif t=='}': stack.pop(); i+=1
        elif t=='OFFSET': joints[cur]['offset']=np.array(list(map(float,toks[i+1:i+4]))); i+=4
        elif t=='CHANNELS':
            n=int(toks[i+1]); joints[cur]['ch']=toks[i+2:i+2+n]; i+=2+n
        else: i+=1
    lines=mot.strip().split('\n')
    nf=int(lines[0].split(':')[1]); ft=float(lines[1].split(':')[1])
    data=np.array([list(map(float,l.split())) for l in lines[2:2+nf]])
    if drop_first: data=data[1:]
    F=len(data); col=0
    G=[None]*len(joints); P=[None]*len(joints); LR=[None]*len(joints)
    for j,jt in enumerate(joints):
        R=np.tile(np.eye(3),(F,1,1)); pos=np.tile(jt['offset'],(F,1)).astype(float)
        for c in jt['ch']:
            v=data[:,col]; col+=1
            if c.endswith('position'): pos[:,'XYZ'.index(c[0])]=v
            else: R=R@rotm(c[0],v)
        LR[j]=R
        if jt['parent']<0: G[j]=R; P[j]=pos
        else:
            p=jt['parent']; P[j]=P[p]+np.einsum('fij,fj->fi',G[p],pos); G[j]=G[p]@R
    names=[j['name'] for j in joints]
    pos=np.stack(P,1)*UNIT  # F,J,3 metres, right-handed Y-up
    ends={}
    for j,jt in enumerate(joints):
        if jt['end'] is not None: ends[jt['name']]=(P[j]+np.einsum('fij,j->fi',G[j],jt['end']))*UNIT
    return dict(names=names,parents=[j['parent'] for j in joints],pos=pos,ends=ends,dt=ft,F=F,
                local=np.stack(LR,1), offsets=np.array([j['offset'] for j in joints])*UNIT)
