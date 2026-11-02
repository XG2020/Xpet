"""Explicit derived skin repairs for the two supplied whale profiles.

Skin-color seeds and welded mesh connectivity locate complete palm/finger
surfaces. The source GLB is never modified; repairs are recorded in metadata.
"""
from collections import defaultdict, deque
import heapq
import math
import struct
import zlib


def _png_rgb(reference):
    if not isinstance(reference,dict) or reference.get('suffix')!='.png':return None
    raw=reference['data']
    if len(raw)<33 or raw[:8]!=b'\x89PNG\r\n\x1a\n':return None
    width,height,depth,color,compression,filtering,interlace=struct.unpack('>IIBBBBB',raw[16:29])
    if depth!=8 or color not in (2,6) or interlace or compression or filtering or not 0<width*height<=16_777_216:return None
    channels=3 if color==2 else 4;stride=width*channels;at=8;chunks=[]
    while at<len(raw):
        if at+12>len(raw):return None
        n,=struct.unpack_from('>I',raw,at)
        if at+12+n>len(raw):return None
        if raw[at+4:at+8]==b'IDAT':chunks.append(raw[at+8:at+8+n])
        at+=12+n
    expected=(stride+1)*height;decoder=zlib.decompressobj()
    encoded=decoder.decompress(b''.join(chunks),expected+1)
    if len(encoded)!=expected or not decoder.eof:raise ValueError('模型颜色贴图解码长度无效。')
    decoded=bytearray(stride*height);at=0;previous=bytearray(stride)
    for y in range(height):
        kind=encoded[at];at+=1;row=bytearray(encoded[at:at+stride]);at+=stride
        if kind==1:
            for x in range(channels,stride):row[x]=(row[x]+row[x-channels])&255
        elif kind==2:row=bytearray((a+b)&255 for a,b in zip(row,previous))
        elif kind in (3,4):
            for x in range(stride):
                a=row[x-channels] if x>=channels else 0;b=previous[x];c=previous[x-channels] if x>=channels else 0
                if kind==3:v=(a+b)//2
                else:
                    p=a+b-c;pa,pb,pc=abs(p-a),abs(p-b),abs(p-c);v=a if pa<=pb and pa<=pc else b if pb<=pc else c
                row[x]=(row[x]+v)&255
        elif kind!=0:return None
        decoded[y*stride:(y+1)*stride]=row;previous=row
    return width,height,channels,decoded


def _unit(vector):
    length=math.sqrt(sum(x*x for x in vector))
    return tuple(x/max(length,1e-12) for x in vector)


def _distance(p,a,b):
    d=tuple(b[k]-a[k] for k in range(3));length=sum(x*x for x in d)
    t=max(0.,min(1.,sum((p[k]-a[k])*d[k] for k in range(3))/max(length,1e-12)))
    return t,math.sqrt(sum((p[k]-a[k]-d[k]*t)**2 for k in range(3)))


def _write_weights(vertex,influence):
    combined=defaultdict(float)
    for b,w in influence:
        if w>1e-8:combined[b]+=w
    influence=sorted(combined.items(),key=lambda p:p[1],reverse=True)[:4]
    total=sum(w for b,w in influence);influence=[(b,w/total) for b,w in influence]+[(-1,0.)]*(4-len(influence))
    return tuple(vertex[:8])+tuple(b for b,w in influence)+tuple(w for b,w in influence)


def _continuous_sleeves(model,hand_masks,arm_chains,positions,adjacency,members,vertex_group,body_ids,child):
    """A single geodesic field crosses the source's arm/pelvis hard seam.

    True palms are wrist anchors. The trunk, authored skirt/tail weights and
    distant garments are body anchors; unclassified pelvis sleeve points are
    interpolated instead of being mistaken for fixed torso vertices.
    """
    vertices=model['vertices'];bones=model['bones'];lookup={b['name']:i for i,b in enumerate(bones)}
    names=[b['name'] for b in bones];h=model['metadata']['target_height_m'];cx=bones[lookup['頭' if child else 'pelvis']]['position'][0]
    original={g:{b:sum(w for i in ids for bone,w in zip(vertices[i][8:12],vertices[i][12:16]) if bone==b)/len(ids)
                 for b in {bone for i in ids for bone,w in zip(vertices[i][8:12],vertices[i][12:16]) if bone>=0 and w>0}}
              for g,ids in members.items()}
    all_hands=set().union(*hand_masks.values());hand_groups={vertex_group[i] for i in all_hands}
    arm_bones={lookup[n] for chain in arm_chains.values() for n in chain[0]}
    body_anchors=set()
    for g,p in positions.items():
        if g in hand_groups:continue
        weight=original[g]
        head=sum(w for b,w in weight.items() if names[b].lower() in ('head','neck','頭','首'))
        skirt=sum(w for b,w in weight.items() if names[b].lower().startswith(('skirt','tail')))
        core=abs(p[0]-cx)<(.085*h) and p[1]<(h*.90)
        lower=p[1]<(h*.22 if child else .72)
        if head>.9 or skirt>.8 or core or lower:body_anchors.add(g)
    edge_lengths={g:[(other,max(1e-7,math.dist(p,positions[other]))) for other in adjacency[g]] for g,p in positions.items()}
    def distances(seeds,limit):
        result={g:0. for g in seeds};heap=[(0.,g) for g in seeds];heapq.heapify(heap)
        while heap:
            cost,a=heapq.heappop(heap)
            if cost!=result[a]:continue
            for b,length in edge_lengths[a]:
                total=cost+length
                if total<=limit and total<result.get(b,float('inf')):result[b]=total;heapq.heappush(heap,(total,b))
        return result
    body_dist=distances(body_anchors,h*.6);fields={};domain=set()
    for side,(chain,shoulder,elbow,wrist) in arm_chains.items():
        indices={lookup[n] for n in chain};seeds={g for g,w in original.items() if sum(w.get(b,0.) for b in indices)>.65}
        seeds.update(vertex_group[i] for i in hand_masks[side]);seeds-=body_anchors
        d_arm=distances(seeds,h*.22);field={}
        for g,d in d_arm.items():
            if g in body_anchors:field[g]=0.;continue
            db=body_dist.get(g,h*.6);alpha=db/max(1e-12,db+d)
            taper=max(0.,min(1.,(h*.22-d)/(h*.1)));taper=taper*taper*(3-2*taper)
            field[g]=alpha*taper;domain.add(g)
        fields[side]=field
    def torso(p):
        keys=('下半身','上半身','上半身2') if child else ('pelvis','spine','chest')
        nodes=sorted((bones[lookup[n]]['position'][1],lookup[n]) for n in keys)
        if p[1]<=nodes[0][0]:return [(nodes[0][1],1.)]
        if p[1]>=nodes[-1][0]:return [(nodes[-1][1],1.)]
        for (a,i),(b,j) in zip(nodes,nodes[1:]):
            if a<=p[1]<=b:
                t=(p[1]-a)/(b-a);return [(i,1-t),(j,t)]
    changed=0
    for g in domain|body_anchors:
        ids=members[g];p=positions[g]
        if any(i in all_hands for i in ids):continue
        alphas={s:f.get(g,0.) for s,f in fields.items()};side=max(alphas,key=alphas.get);alpha=alphas[side]
        chain,shoulder,elbow,wrist=arm_chains[side];t,d1=_distance(p,shoulder,elbow);u,d2=_distance(p,elbow,wrist)
        arm=[(lookup[chain[0]],1-t),(lookup[chain[1]],t)] if d1<d2 else [(lookup[chain[1]],1-u),(lookup[chain[2]],u)]
        body=[(b,w) for b,w in original[g].items() if b not in arm_bones]
        if child and sum(w for b,w in body)>.5 and .29<p[1]<.49:body=torso(p)
        elif not body:body=torso(p)
        total=sum(w for b,w in body);body=[(b,w/total) for b,w in body]
        influences=[(b,w*alpha) for b,w in arm]+[(b,w*(1-alpha)) for b,w in body]
        for i in ids:
            replacement=_write_weights(vertices[i],influences)
            if replacement[8:]!=vertices[i][8:]:changed+=1
            vertices[i]=replacement
    model['metadata']['sleeve_skin_adaptation']='welded mesh geodesic arm/body field; continuous forearm/wrist proportions'
    model['metadata']['sleeve_graph_repaired_vertices']=changed
    model['metadata']['sleeve_graph_body_anchor_vertices']=sum(len(members[g]) for g in body_anchors)
    if child:model['metadata']['generated_rig_revision']=4


def _separate_sleeve_surfaces(model,original,hand_masks,arm_chains,child,colors):
    """Split the AI mesh's sleeve/skirt bridge at derived barycentric seams.

    Mixed triangles are partitioned into three coplanar triangles. Each side
    owns a separate copy of its midpoint, so no triangle stretches from wrist
    to skirt. Bind surface area, UV interpolation and morphs are preserved.
    """
    vertices=model['vertices'];preserved=list(vertices);bones=model['bones'];names=[b['name'] for b in bones];lookup={n:i for i,n in enumerate(names)};h=model['metadata']['target_height_m']
    cx=bones[lookup['頭' if child else 'pelvis']]['position'][0];hands=set().union(*hand_masks.values())
    head={r[0] for m in model['morphs'] if m['name']=='Head_Nod' for r in m['offsets'] if sum(v*v for v in r[1:])>1e-12} if child else set()
    arm_indices={s:{lookup[n] for n in chain[0]} for s,chain in arm_chains.items()};all_arm=set().union(*arm_indices.values())
    labels={};start=0;body_material=None
    for material in model['materials']:
        ids=set(model['indices'][start:start+material['count']]);start+=material['count']
        if 'skin_full' in material['name'].lower() or 'skin_v3' in material['name'].lower():body_material=material;body_ids=ids;break
    if body_material is None:return
    def upper_tube(p,side):
        chain,shoulder,elbow,wrist=arm_chains[side];t,d=_distance(p,shoulder,elbow)
        return t,d
    def shoulder_root(i):
        p=preserved[i][:3]
        return any(0<=t<.2 and d<.05*h for side in arm_chains for t,d in [upper_tube(p,side)])
    for i in body_ids:
        p=vertices[i][:3];side='L' if p[0]>=cx else 'R';label=0
        if i in hands:label=1 if i in hand_masks['L'] else 2
        elif child:
            chain,shoulder,elbow,wrist=arm_chains[side];_,d1=_distance(p,shoulder,elbow);_,d2=_distance(p,elbow,wrist)
            t,d=upper_tube(p,side);true_upper=0<t<1 and d<.035*h
            if i not in head and (true_upper or (p[1]>wrist[1]-.012*h and p[1]<.52*h and abs(p[0]-cx)>.095*h and p[2]<.035*h and min(d1,d2)<.05*h)):
                label=1 if side=='L' else 2
        else:
            sums={s:sum(w for b,w in zip(original[i][8:12],original[i][12:16]) if b in indices) for s,indices in arm_indices.items()}
            tube=[(d,s) for s in arm_chains for t,d in [upper_tube(p,s)] if 0<t<1 and d<.035*h]
            if tube:
                side=min(tube)[1];label=1 if side=='L' else 2
            else:
                side=max(sums,key=sums.get)
                if sums[side]>.5:label=1 if side=='L' else 2
        labels[i]=label
    def body_weights(p,refs):
        result=defaultdict(float)
        for i,factor in refs:
            for b,w in zip(original[i][8:12],original[i][12:16]):
                if b>=0 and b not in all_arm:result[b]+=w*factor
        if child and .29<p[1]<.49 and not any(i in head for i,w in refs):result.clear()
        if result:return list(result.items())
        keys=('下半身','上半身','上半身2') if child else ('pelvis','spine','chest')
        nodes=sorted((bones[lookup[k]]['position'][1],lookup[k]) for k in keys)
        if p[1]<=nodes[0][0]:return [(nodes[0][1],1.)]
        if p[1]>=nodes[-1][0]:return [(nodes[-1][1],1.)]
        for (a,i),(b,j) in zip(nodes,nodes[1:]):
            if a<=p[1]<=b:
                t=(p[1]-a)/(b-a);return [(i,1-t),(j,t)]
    def weights(label,p,refs):
        if label==0:return body_weights(p,refs)
        side='L' if label==1 else 'R';chain,shoulder,elbow,wrist=arm_chains[side]
        if all(i in hand_masks[side] for i,w in refs):return [(lookup[chain[2]],1.)]
        t,d1=_distance(p,shoulder,elbow);u,d2=_distance(p,elbow,wrist)
        influence=[(lookup[chain[0]],1-t),(lookup[chain[1]],t)] if d1<d2 else [(lookup[chain[1]],1-u),(lookup[chain[2]],u)]
        # Retain a continuous shoulder attachment within the arm-side surface.
        along=math.dist(shoulder,elbow)*t if d1<d2 else math.dist(shoulder,elbow)+math.dist(elbow,wrist)*u
        alpha=max(0.,min(1.,along/(h*.055)));alpha=alpha*alpha*(3-2*alpha)
        return [(b,w*alpha) for b,w in influence]+[(b,w*(1-alpha)) for b,w in body_weights(p,refs)]
    for i,label in labels.items():
        if not shoulder_root(i):vertices[i]=_write_weights(vertices[i],weights(label,vertices[i][:3],[(i,1.)]))
    derived={};midpoints={};new_indices=[];split=0;start=0
    def midpoint(a,b,label):
        key=(min(a,b),max(a,b),label)
        if key in midpoints:return midpoints[key]
        av,bv=vertices[a],vertices[b];base=tuple((av[k]+bv[k])*.5 for k in range(8));normal=_unit(base[3:6]);base=base[:3]+normal+base[6:]
        index=len(vertices);vertices.append(_write_weights(base+(-1,)*4+(0.,)*4,weights(label,base[:3],[(a,.5),(b,.5)])))
        midpoints[key]=index;derived[index]=[(a,.5),(b,.5)];labels[index]=label;return index
    for material in model['materials']:
        old=model['indices'][start:start+material['count']];start+=material['count'];part=[]
        if material is not body_material:part=old
        else:
            for at in range(0,len(old),3):
                tri=old[at:at+3];tags=[labels[i] for i in tri];unique=set(tags)
                if len(unique)==1:part.extend(tri);continue
                # Keep only the true shoulder root continuous. Skin-to-dress
                # contact is also an erroneous mesh bridge and must separate.
                if any(shoulder_root(i) for i in tri):
                    part.extend(tri);continue
                if len(unique)==2:
                    minority=next(tag for tag in unique if tags.count(tag)==1);j=tags.index(minority);a,b,c=tri[j],tri[(j+1)%3],tri[(j+2)%3];majority=labels[b]
                    ab_a=midpoint(a,b,minority);ac_a=midpoint(a,c,minority);ab_b=midpoint(a,b,majority);ac_b=midpoint(a,c,majority)
                    part.extend((a,ab_a,ac_a,b,c,ac_b,b,ac_b,ab_b));split+=1
                else:
                    # No ordinary humanoid face spans both arms; retain rare
                    # three-way faces on the body rather than building a bridge.
                    for i in tri:vertices[i]=_write_weights(vertices[i],body_weights(vertices[i][:3],[(i,1.)]));labels[i]=0
                    part.extend(tri)
        material['count']=len(part);new_indices.extend(part)
    model['indices']=new_indices
    values=sum(len(m['offsets']) for m in model['morphs'])
    for morph in model['morphs']:
        offsets={r[0]:r[1:] for r in morph['offsets']}
        for index,refs in derived.items():
            delta=tuple(sum(offsets.get(i,(0.,0.,0.))[k]*factor for i,factor in refs) for k in range(3))
            if any(abs(v)>1e-10 for v in delta):morph['offsets'].append((index,*delta));values+=1
    if len(vertices)>600000 or len(new_indices)>3000000 or values>4000000:raise ValueError('袖裙接缝分离后的模型资源超过限制。')
    model['_derived_vertex_sources']=derived;model['_surface_labels']=labels
    model['metadata'].update({'derived_topology_adaptation':'sleeve/skirt seam separation with duplicated barycentric edge midpoints',
                              'separated_bridge_triangles':split,'derived_boundary_vertices':len(derived),
                              'source_bind_surface_preserved':True,'source_glb_modified':False})
    model['metadata']['protected_upper_arm_attachment']=True
    model['metadata']['topology_adaptation_revision']=8
    if child:model['metadata']['generated_rig_revision']=8


def adapt_whale_hands(model):
    profile=model.get('metadata',{}).get('rig_profile','')
    child=profile.startswith('whale-child')
    if profile!='whale-adult' and not child:return
    material=next((m for m in model['materials'] if 'skin_full' in m['name'].lower() or 'skin_v3' in m['name'].lower()),None)
    if material is None:return
    image=_png_rgb(material['texture'])
    if image is None:return
    width,height,channels,decoded=image;vertices=model['vertices'];original=list(vertices);bones=model['bones'];lookup={b['name']:i for i,b in enumerate(bones)}
    first=0;body_ids=set();body_triangles=[]
    for m in model['materials']:
        indices=model['indices'][first:first+m['count']];first+=m['count']
        if m is material:body_ids=set(indices);body_triangles=indices;break
    def color(index):
        v=vertices[index];x=max(0,min(width-1,round(v[6]*(width-1))));y=max(0,min(height-1,round((1-v[7])*(height-1))))
        at=(y*width+x)*channels;return decoded[at],decoded[at+1],decoded[at+2]
    colors={i:color(i) for i in body_ids}
    strong=lambda rgb:rgb[0]>120 and rgb[1]>75 and rgb[2]>50 and rgb[0]-rgb[1]>=10 and rgb[0]-rgb[2]>=9
    weak=lambda rgb:rgb[0]>95 and rgb[1]>65 and rgb[2]>45 and rgb[0]-rgb[1]>=5 and rgb[0]-rgb[2]>=5
    # Weld coincident glTF vertices so a UV or normal seam cannot drop fingers.
    groups={};members=defaultdict(list);vertex_group={}
    for i in body_ids:
        key=tuple(round(v*1_000_000) for v in vertices[i][:3]);group=groups.setdefault(key,len(groups));members[group].append(i);vertex_group[i]=group
    positions={group:vertices[ids[0]][:3] for group,ids in members.items()};adjacency=defaultdict(set)
    for at in range(0,len(body_triangles),3):
        tri=[vertex_group[i] for i in body_triangles[at:at+3]]
        for a,b in ((tri[0],tri[1]),(tri[1],tri[2]),(tri[2],tri[0])):
            if a!=b:adjacency[a].add(b);adjacency[b].add(a)
    model_height=model['metadata'].get('target_height_m',1.0 if child else 1.65)
    hand_masks={};reports={};arm_chains={}
    for side,sign in (('L',1),('R',-1)):
        names=('腕.'+side,'ひじ.'+side,'手首.'+side) if child else ('upper_arm.'+side,'forearm.'+side,'hand.'+side)
        if any(name not in lookup for name in names):return
        shoulder,elbow,wrist=(bones[lookup[name]]['position'] for name in names)
        axis=_unit(tuple(wrist[k]-elbow[k] for k in range(3)));h=model_height
        def pocket(p,distal_extra=.0):
            delta=tuple(p[k]-wrist[k] for k in range(3));t=sum(delta[k]*axis[k] for k in range(3))
            radius=math.sqrt(max(0.,sum(x*x for x in delta)-t*t))
            return -.055*h<=t<(.16+distal_extra)*h and radius<(.08 if child else .065)*h and p[1]<wrist[1]+(.06 if child else .018)*h
        seeds={vertex_group[i] for i in body_ids if pocket(vertices[i][:3]) and strong(colors[i])}
        if len(seeds)<12:continue
        # Grow over skin and small shading gaps, retaining all copies at seams.
        allowed={g for g,p in positions.items() if pocket(p,.025)}
        selected=set(seeds);queue=deque((g,0.) for g in seeds);best={g:0. for g in seeds}
        while queue:
            a,gap=queue.popleft()
            for b in adjacency[a]:
                if b not in allowed:continue
                # Navy sleeve/trim pixels are never admitted as a skin shading
                # gap; the old weak expansion could turn whole cuffs into hand.
                if all(colors[i][2]>colors[i][0]*1.12 for i in members[b]):continue
                d=math.dist(positions[a],positions[b]);skin=any(weak(colors[i]) for i in members[b]);cost=0. if skin else gap+d
                if cost>.004*h or cost>=best.get(b,float('inf')):continue
                best[b]=cost;selected.add(b);queue.append((b,cost))
        hand={i for group in selected for i in members[group]}
        # A hand component must contain actual distal geometry, not a cuff-only
        # patch. Strong seeds are retained even on an isolated fingertip island.
        hand.update(i for g in seeds for i in members[g]);hand_masks[side]=hand
        projections=[(sum((vertices[i][k]-wrist[k])*axis[k] for k in range(3)),i) for i in hand if strong(colors[i])]
        projections.sort();proximal=projections[:max(1,int(len(projections)*.12))]
        if child and proximal:
            wrist=tuple(sum(vertices[i][k] for _,i in proximal)/len(proximal) for k in range(3))
            bones[lookup[names[2]]]['position']=wrist
            # Fit the generated elbow to the native lowered sleeve and wrist.
            elbow=tuple(shoulder[k]*.48+wrist[k]*.52 for k in range(3));bones[lookup[names[1]]]['position']=elbow
        arm_chains[side]=(names,shoulder,elbow,wrist)
        hand_bone=lookup[names[2]];wrong=sum(sum(w for b,w in zip(vertices[i][8:12],vertices[i][12:16]) if b==hand_bone)<.999 for i in hand)
        for i in hand:vertices[i]=_write_weights(vertices[i],[(hand_bone,1.)])
        reports[side]={'skin_seed_welded_vertices':len(seeds),'complete_hand_vertices':len(hand),'source_non_hand_weights_repaired':wrong,
                       'wrist_position':wrist,'hand_bounds':{'min':[min(vertices[i][k] for i in hand) for k in range(3)],'max':[max(vertices[i][k] for i in hand) for k in range(3)]}}
    if child and len(hand_masks)==2:
        head_mask={r[0] for m in model['morphs'] if m['name']=='Head_Nod' for r in m['offsets'] if sum(x*x for x in r[1:])>1e-12}
        cx=bones[lookup['頭']]['position'][0];bottom=min(v[1] for v in vertices);h=model_height;all_hands=hand_masks['L']|hand_masks['R'];sleeves=set();garments=set()
        def torso(y):
            t=max(0.,min(1.,(y-.35)/.12))
            return [(lookup['上半身'],1-t),(lookup['上半身2'],t)] if y>.35 else [(lookup['下半身'],1.)]
        for i in body_ids-all_hands:
            p=vertices[i][:3];x=(p[0]-cx)/h;y=(p[1]-bottom)/h;z=p[2]/h;side='L' if x>=0 else 'R'
            if i in head_mask:continue
            names,shoulder,elbow,wrist=arm_chains[side];t,d1=_distance(p,shoulder,elbow);u,d2=_distance(p,elbow,wrist)
            # Below the measured wrist, only the connected hand/cuff surface may
            # follow the arm. Broad skirt/apron regions remain on the torso.
            garment=y<(wrist[1]-bottom)/h-.015 or (abs(x)<.11 and y<.49) or z>.035
            radius=.046*h;d=min(d1,d2);factor=max(0.,min(1.,(radius-d)/(.04*h))) if not garment and .28<y<.52 and abs(x)>.07 else 0.
            factor=factor*factor*(3-2*factor)
            if factor>0:
                arm=[(lookup[names[0]],1-t),(lookup[names[1]],t)] if d1<d2 else [(lookup[names[1]],1-u),(lookup[names[2]],u)]
                influence=[(b,w*factor) for b,w in arm]+[(b,w*(1-factor)) for b,w in torso(y)];sleeves.add(i)
            else:influence=torso(y);garments.add(i)
            vertices[i]=_write_weights(vertices[i],influence)
        model['metadata'].update({'generated_rig_revision':3,'generated_hand_vertices':{s:len(m) for s,m in hand_masks.items()},
                                  'generated_sleeve_vertices':len(sleeves),'generated_garment_no_arm_vertices':len(garments)})
    if reports:
        if len(hand_masks)==2:
            _continuous_sleeves(model,hand_masks,arm_chains,positions,adjacency,members,vertex_group,body_ids,child)
            _separate_sleeve_surfaces(model,original,hand_masks,arm_chains,child,colors)
        model['metadata']['hand_skin_adaptation']='skin-color-seeds + welded mesh connectivity; palm/fingers rigid to wrist, no generated finger joints'
        model['metadata']['hand_region_diagnostics']=reports
        model['_hand_masks']=hand_masks
