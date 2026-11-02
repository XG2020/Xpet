"""Bounded glTF 2 GLB reader for the existing portable PETRAW1 pipeline.

Authored skeletons, embedded color images and vertex shape targets are retained.
The supplied whale profiles receive explicit palm/sleeve skin repairs, recorded
in metadata; their original GLB files and all other model weights are preserved.
The supplied whale-child profile is the only unskinned model for which a basic
rig is generated; this is an approximation, explicitly recorded in metadata.
"""
from __future__ import annotations

import base64
import json
import math
from pathlib import Path
import struct

from runtime_models import MAX_BONES, MAX_INDICES, MAX_MORPH_VALUES, MAX_VERTICES
from runtime_models import _finite, _matrix_product, _point, _quaternion, validate_rig

IDENTITY = ((1.,0.,0.,0.), (0.,1.,0.,0.), (0.,0.,1.,0.), (0.,0.,0.,1.))
REFLECT = ((1.,0.,0.,0.), (0.,1.,0.,0.), (0.,0.,-1.,0.), (0.,0.,0.,1.))
COMPONENTS = {5120: ('b',1), 5121: ('B',1), 5122: ('h',2), 5123: ('H',2), 5125: ('I',4), 5126: ('f',4)}
WIDTHS = {'SCALAR':1, 'VEC2':2, 'VEC3':3, 'VEC4':4, 'MAT4':16}


def _integer(value, maximum, label, minimum=0):
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError('GLB '+label+'无效或超限。')
    return value


def _index(rows, value, label):
    return rows[_integer(value, len(rows)-1, label)]


def _vector(value, count, default):
    if value is None: return default
    if not isinstance(value, list) or len(value) != count or any(type(x) not in (float,int) for x in value):
        raise ValueError('GLB 变换数据无效。')
    return _finite(tuple(value))


def _local_matrix(node):
    if 'matrix' in node:
        flat = _vector(node['matrix'],16,None)
        return tuple(tuple(flat[c*4+r] for c in range(4)) for r in range(4))
    x,y,z,w = _vector(node.get('rotation'),4,(0.,0.,0.,1.))
    length = math.sqrt(x*x+y*y+z*z+w*w)
    if length < 1e-8: raise ValueError('GLB 骨骼旋转无效。')
    x,y,z,w = (v/length for v in (x,y,z,w))
    scale = _vector(node.get('scale'),3,(1.,1.,1.))
    if any(abs(v)<1e-8 for v in scale): raise ValueError('GLB 含零缩放变换。')
    p = _vector(node.get('translation'),3,(0.,0.,0.))
    rotation = ((1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)),
                (2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)),
                (2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)))
    return tuple(tuple(rotation[r][c]*scale[c] for c in range(3))+(p[r],) for r in range(3))+((0.,0.,0.,1.),)


def _direction(matrix, vector):
    return tuple(sum(matrix[r][c]*vector[c] for c in range(3)) for r in range(3))


def _normal(matrix, vector):
    # Inverse transpose preserves normals under nonuniform mesh-node scaling.
    a,b,c = matrix[0][:3]; d,e,f = matrix[1][:3]; g,h,i = matrix[2][:3]
    cofactors = ((e*i-f*h,f*g-d*i,d*h-e*g),(c*h-b*i,a*i-c*g,b*g-a*h),(b*f-c*e,c*d-a*f,a*e-b*d))
    det = a*cofactors[0][0]+b*cofactors[0][1]+c*cofactors[0][2]
    if abs(det)<1e-12: raise ValueError('GLB 蒙皮变换不可逆。')
    return tuple(sum(cofactors[r][k]*vector[k] for k in range(3))/det for r in range(3))


def _unit(vector):
    length = math.sqrt(sum(v*v for v in vector))
    return tuple(v/length for v in vector) if length>1e-10 else (0.,1.,0.)


class Glb:
    def __init__(self, source):
        if source.stat().st_size > 1024*1024*1024: raise ValueError('GLB 文件不能超过 1 GB。')
        self.raw = source.read_bytes()
        if len(self.raw)<20: raise ValueError('GLB 文件头不完整。')
        magic,version,length = struct.unpack_from('<4sII',self.raw)
        if magic!=b'glTF' or version!=2 or length!=len(self.raw): raise ValueError('支持完整的 glTF 2.0 GLB 文件。')
        at = 12; self.doc = None; self.binary = memoryview(b'')
        while at < length:
            if at+8>length: raise ValueError('GLB 数据块不完整。')
            size,kind = struct.unpack_from('<II',self.raw,at); at+=8
            if size%4 or at+size>length: raise ValueError('GLB 数据块长度无效。')
            chunk = memoryview(self.raw)[at:at+size]; at+=size
            if kind==0x4e4f534a:
                if self.doc is not None or at-size!=20 or size>16*1024*1024: raise ValueError('GLB JSON 数据块无效或超限。')
                try: self.doc=json.loads(bytes(chunk))
                except (ValueError,UnicodeError): raise ValueError('GLB JSON 数据无效。') from None
            elif kind==0x004e4942:
                if self.binary: raise ValueError('GLB 包含多个二进制数据块。')
                self.binary=chunk
        if not isinstance(self.doc,dict): raise ValueError('GLB 缺少场景数据。')
        if self.doc.get('asset',{}).get('version')!='2.0': raise ValueError('支持 glTF 2.0 模型。')
        if set(self.doc.get('extensionsRequired',[]))-{'KHR_materials_unlit'}:
            raise ValueError('GLB 使用未支持的必需扩展，请导出未压缩的 glTF 2.0。')
        for name,limit in (('nodes',MAX_BONES),('meshes',4096),('skins',4096),('materials',4096),('accessors',32768),('bufferViews',32768),('images',4096),('textures',4096)):
            rows=self.doc.get(name,[])
            if not isinstance(rows,list) or len(rows)>limit or any(not isinstance(row,dict) for row in rows):
                raise ValueError('GLB '+name+'数据无效或超限。')
        buffers=self.doc.get('buffers',[])
        if len(buffers)!=1 or buffers[0].get('uri'):
            raise ValueError('GLB 仅支持内嵌的模型二进制数据。')
        declared=_integer(buffers[0].get('byteLength'),len(self.binary),'二进制长度')
        if len(self.binary)-declared>3: raise ValueError('GLB 二进制长度不一致。')
        self.binary=self.binary[:declared]

    def view(self, reference):
        view=_index(self.doc.get('bufferViews',[]),reference,'bufferView 索引')
        if view.get('buffer',0)!=0: raise ValueError('GLB 引用了外部二进制数据。')
        offset=_integer(view.get('byteOffset',0),len(self.binary),'bufferView 偏移')
        size=_integer(view.get('byteLength'),len(self.binary)-offset,'bufferView 长度')
        stride=view.get('byteStride')
        if stride is not None:
            _integer(stride,252,'bufferView 步长',4)
            if stride%4: raise ValueError('GLB bufferView 步长没有对齐。')
        return self.binary[offset:offset+size],stride

    def accessor(self, reference, expected=None, maximum=MAX_MORPH_VALUES):
        accessor=_index(self.doc.get('accessors',[]),reference,'accessor 索引')
        kind=accessor.get('type'); component=accessor.get('componentType')
        if kind not in WIDTHS or component not in COMPONENTS or (expected and kind!=expected):
            raise ValueError('GLB 顶点属性类型无效。')
        if kind=='MAT4' and component!=5126: raise ValueError('GLB 绑定矩阵必须使用浮点数。')
        count=_integer(accessor.get('count'),maximum,'accessor 数量')
        fmt,component_size=COMPONENTS[component]; width=WIDTHS[kind]; element_size=component_size*width
        layout=struct.Struct('<'+fmt*width)
        normalized=accessor.get('normalized',False)
        if type(normalized) is not bool or (normalized and component not in (5120,5121,5122,5123)):
            raise ValueError('GLB 顶点归一化类型无效。')
        def unpack(view,offset,stride,n):
            _integer(offset,len(view),'属性偏移')
            end=offset+(n-1)*stride+element_size if n else offset
            if offset%component_size or stride<element_size or end>len(view):
                raise ValueError('GLB accessor 超出二进制数据范围。')
            return [layout.unpack_from(view,offset+j*stride) for j in range(n)]
        offset=_integer(accessor.get('byteOffset',0),len(self.binary),'accessor 偏移')
        if 'bufferView' in accessor:
            view,stride=self.view(accessor['bufferView']);rows=unpack(view,offset,stride or element_size,count)
        else:
            if offset: raise ValueError('GLB 稀疏属性的基准偏移无效。')
            rows=[(0,)*width for _ in range(count)]
        if 'sparse' in accessor:
            sparse=accessor['sparse'];n=_integer(sparse.get('count'),count,'稀疏属性数量')
            info=sparse['indices'];code=info.get('componentType')
            if code not in (5121,5123,5125): raise ValueError('GLB 稀疏索引类型无效。')
            ids,stride=self.view(info['bufferView']);idfmt,idsize=COMPONENTS[code]
            off=_integer(info.get('byteOffset',0),len(ids),'稀疏索引偏移')
            if stride or off+n*idsize>len(ids): raise ValueError('GLB 稀疏索引长度无效。')
            ids=[struct.unpack_from('<'+idfmt,ids,off+j*idsize)[0] for j in range(n)]
            if any(not 0<=v<count for v in ids) or any(ids[j]>=ids[j+1] for j in range(n-1)):
                raise ValueError('GLB 稀疏索引越界或重复。')
            info=sparse['values'];view,stride=self.view(info['bufferView'])
            if stride: raise ValueError('GLB 稀疏属性不能包含步长。')
            values=unpack(view,info.get('byteOffset',0),element_size,n)
            for index,value in zip(ids,values):rows[index]=value
        if normalized:
            divisor={5120:127,5121:255,5122:32767,5123:65535}[component]
            rows=[tuple(max(-1.,v/divisor) for v in row) for row in rows]
        for row in rows:_finite(row)
        return rows

    def worlds(self):
        nodes=self.doc.get('nodes',[]);parents=[-1]*len(nodes)
        for i,node in enumerate(nodes):
            children=node.get('children',[])
            if not isinstance(children,list): raise ValueError('GLB 节点子级数据无效。')
            for child in children:
                _integer(child,len(nodes)-1,'节点子级')
                if parents[child]!=-1 or child==i: raise ValueError('GLB 节点包含重复父级或循环。')
                parents[child]=i
        worlds=[None]*len(nodes);visiting=set()
        def world(index):
            if index in visiting: raise ValueError('GLB 节点层级形成循环。')
            if worlds[index] is not None:return worlds[index]
            visiting.add(index)
            parent=world(parents[index]) if parents[index]>=0 else IDENTITY
            worlds[index]=_matrix_product(parent,_local_matrix(nodes[index]));visiting.remove(index)
            _finite(tuple(v for row in worlds[index] for v in row))
            return worlds[index]
        for i in range(len(nodes)):world(i)
        scenes=self.doc.get('scenes',[])
        if scenes:
            roots=_index(scenes,self.doc.get('scene',0),'场景索引').get('nodes',[])
        else:roots=[i for i,parent in enumerate(parents) if parent==-1]
        active=set()
        def visit(index):
            _integer(index,len(nodes)-1,'场景节点')
            if index in active:return
            active.add(index)
            for child in nodes[index].get('children',[]):visit(child)
        for root in roots:visit(root)
        return worlds,parents,active

    def image(self, index):
        image=_index(self.doc.get('images',[]),index,'图像索引')
        if 'bufferView' in image:
            raw,stride=self.view(image['bufferView'])
            if stride or len(raw)>64*1024*1024: raise ValueError('GLB 内嵌贴图无效或超过 64 MB。')
            mime=image.get('mimeType');raw=bytes(raw)
        elif str(image.get('uri','')).startswith('data:'):
            uri=image['uri']
            if len(uri)>90*1024*1024 or ';base64,' not in uri: raise ValueError('GLB 内嵌贴图无效或超限。')
            header,encoded=uri.split(';base64,',1);mime=header[5:]
            try:raw=base64.b64decode(encoded,validate=True)
            except ValueError:raise ValueError('GLB 内嵌贴图编码无效。') from None
        else:return str(image.get('uri',''))
        if mime not in ('image/png','image/jpeg') or len(raw)>64*1024*1024:
            raise ValueError('GLB 内嵌贴图支持 PNG / JPG，单张最多 64 MB。')
        return {'data':raw,'suffix':'.png' if mime=='image/png' else '.jpg'}

    def material(self, reference, name):
        material=_index(self.doc.get('materials',[]),reference,'材质索引') if reference is not None else {}
        pbr=material.get('pbrMetallicRoughness',{});color=_vector(pbr.get('baseColorFactor'),4,(1.,1.,1.,1.))
        if any(not 0<=v<=1 for v in color):raise ValueError('GLB 材质颜色无效。')
        texture=''
        if 'baseColorTexture' in pbr:
            info=pbr['baseColorTexture']
            if info.get('texCoord',0)!=0 or info.get('extensions'):raise ValueError('GLB 颜色贴图使用未支持的 UV 变换。')
            record=_index(self.doc.get('textures',[]),info['index'],'贴图索引');texture=self.image(record.get('source'))
        return {'name':str(material.get('name',name))[:1024], 'color':color,'texture':texture,
                'double':bool(material.get('doubleSided',False))}


def _profile(source, glb):
    path=source.parent/'asset.json'
    if not path.is_file() or path.stat().st_size>65536:return {}
    try:asset=json.loads(path.read_text(encoding='utf-8-sig'))
    except (ValueError,UnicodeError):return {}
    if not isinstance(asset,dict):return {}
    known={'whale-adult':('C01',1.65),'whale-child':('C02',1.0)}
    slug=asset.get('slug')
    if slug not in known or asset.get('id')!=known[slug][0]:return {}
    expected={'whale-adult':'C01_Full_Animation_Mesh','whale-child':'C02_Full_Working_Mesh'}[slug]
    if expected not in {node.get('name') for node in glb.doc.get('nodes',[])}:return {}
    height=asset.get('height_m',known[slug][1])
    if type(height) not in (int,float) or not math.isfinite(height) or abs(height-known[slug][1])>0.15:
        height=known[slug][1]
    profile={'rig_profile':slug,'target_height_m':float(height),
             'display_name':str(asset.get('name',slug))[:80],'source_license':'CC-BY-4.0'}
    if slug=='whale-adult':
        profile['adaptation_limitations']=['原模型的25根骨骼没有逐指关节；掌指整体随手腕运动，不能独立逐指握拳。']
    return profile


def _basic_child_rig(model):
    """Use the source's deformation regions to isolate head/hair and each arm.

    Leg joints and smooth lower-limb weights are an approximate spatial fit;
    no original bone, finger articulation or clothing physics is claimed.
    """
    vertices=model['vertices'];morphs={m['name']:m['offsets'] for m in model['morphs']}
    if not all(name in morphs for name in ('Head_Nod','Head_Turn','Arm_L','Arm_R','Breath')):
        raise ValueError('幼年模型缺少基础适配所需的原始形变区域。')
    bottom=min(v[1] for v in vertices);height=max(v[1] for v in vertices)-bottom
    if not 0.5<height<1.5:raise ValueError('幼年模型形态与已知基础适配不匹配。')
    cx=(min(v[0] for v in vertices)+max(v[0] for v in vertices))/2
    start=0
    for material in model['materials']:
        selected=set(model['indices'][start:start+material['count']]);start+=material['count']
        if selected and 'projected_lips' in material['name'].lower():
            # The tail and long hair make the full-mesh bounds asymmetric.
            cx=sum(vertices[i][0] for i in selected)/len(selected);break
    masks={name:{row[0] for row in morphs[name] if sum(x*x for x in row[1:])>(height*1e-6)**2}
           for name in ('Head_Nod','Arm_L','Arm_R')}
    if any(len(mask)<3 for mask in masks.values()):raise ValueError('幼年模型形变区域不足，无法生成基础骨架。')
    bones=[];lookup={}
    def add(name,parent,xyz):
        lookup[name]=len(bones);bones.append({'name':name,'parent':lookup.get(parent,-1),
                                            'position':tuple(xyz),'rotation':(0.,0.,0.,1.)})
    def point(x,y,z=0.):return (cx+x*height,bottom+y*height,z*height)
    add('センター',None,point(0,0));add('下半身','センター',point(0,.29))
    add('上半身','下半身',point(0,.36));add('上半身2','上半身',point(0,.44))
    add('首','上半身2',point(0,.49));add('頭','首',point(0,.57))
    arm_chains={}
    for side,sign in (('L',1),('R',-1)):
        selected=[vertices[i] for i in masks['Arm_'+side] if (vertices[i][0]-cx)*sign>height*.06
                  and bottom+height*.23<vertices[i][1]<bottom+height*.43 and vertices[i][2]<height*.025
                  and i not in masks['Head_Nod']]
        if len(selected)<3:raise ValueError('幼年模型手臂形变区域无法定位。')
        selected.sort(key=lambda v:(v[0]-cx)*sign)
        outer=selected[max(0,int(len(selected)*.85)):]
        hand=tuple(sum(v[k] for v in outer)/len(outer) for k in range(3))
        shoulder=point(sign*.09,.445,0)
        elbow=tuple(shoulder[k]*.45+hand[k]*.55 for k in range(3))
        add('肩.'+side,'上半身2',point(sign*.055,.445));add('腕.'+side,'肩.'+side,shoulder)
        add('ひじ.'+side,'腕.'+side,elbow);add('手首.'+side,'ひじ.'+side,hand)
        arm_chains[side]=(shoulder,elbow,hand)
        add('足.'+side,'下半身',point(sign*.07,.285));add('ひざ.'+side,'足.'+side,point(sign*.075,.15))
        add('足首.'+side,'ひざ.'+side,point(sign*.075,.045));add('つま先.'+side,'足首.'+side,point(sign*.075,.025,-.07))
    def mix(a,b,t):return [(lookup[a],1-t),(lookup[b],t)]
    def blend_segment(p,a,b):
        delta=tuple(b[k]-a[k] for k in range(3));denom=sum(x*x for x in delta)
        t=max(0.,min(1.,sum((p[k]-a[k])*delta[k] for k in range(3))/max(denom,1e-12)))
        distance=sum((p[k]-a[k]-delta[k]*t)**2 for k in range(3))
        return t,distance
    def torso_weights(y):
        return mix('上半身','上半身2',max(0.,min(1.,(y-.35)/.12))) if y>.35 else [(lookup['下半身'],1.)]
    guarded_arm_vertices=0
    for index,vertex in enumerate(vertices):
        p=vertex[:3];x=(p[0]-cx)/height;y=(p[1]-bottom)/height;z=p[2]/height
        side='L' if x>=0 else 'R'
        if index in masks['Head_Nod']:
            influence=mix('首','頭',max(0.,min(1.,(y-.46)/.09)))
        elif index in masks['Arm_'+side] and .23<y<.54 and abs(x)>.06 and z<.035:
            shoulder,elbow,hand=arm_chains[side];t,d1=blend_segment(p,shoulder,elbow);u,d2=blend_segment(p,elbow,hand)
            # Source Arm_L/R targets also deform the apron and skirt. A morph
            # mask alone is not a skin label: keep central/lower garments out,
            # and require vertices to lie inside the measured arm capsule.
            garment=y<.285 or (abs(x)<.13 and y<.40) or z>.025
            radius=height*(.032 if y<.35 else .047)
            distance=math.sqrt(min(d1,d2))
            factor=max(0.,min(1.,(radius-distance)/(radius*.4))) if not garment else 0.
            factor=factor*factor*(3-2*factor)
            arm_weights=mix('腕.'+side,'ひじ.'+side,t) if d1<d2 else mix('ひじ.'+side,'手首.'+side,u)
            # Smooth into torso weights along the fused sleeve boundary instead
            # of pulling a broad skirt strip with the hand at full strength.
            influence=[(bone,weight*factor) for bone,weight in arm_weights]+[(bone,weight*(1-factor)) for bone,weight in torso_weights(y)]
            if factor<.999:guarded_arm_vertices+=1
        elif y<.28 and abs(x)<.16 and z<.085:
            if y<.075:influence=[(lookup['足首.'+side],1.)]
            elif y<.19:influence=mix('足首.'+side,'ひざ.'+side,max(0.,min(1.,(y-.075)/.115)))
            else:influence=mix('ひざ.'+side,'足.'+side,max(0.,min(1.,(y-.19)/.09)))
        else:influence=torso_weights(y)
        influence=[(bone,weight) for bone,weight in influence if weight>1e-8]
        total=sum(weight for _,weight in influence);influence=[(bone,weight/total) for bone,weight in influence]
        influence += [(-1,0.)]*(4-len(influence))
        vertices[index]=tuple(vertex[:8])+tuple(b for b,w in influence)+tuple(w for b,w in influence)
    model['bones']=bones
    model['metadata'].update({'rig_source':'generated','rig_profile':'whale-child-basic-v1',
        'adaptation_limitations':['原模型无骨骼；基础关节与蒙皮为桌宠适配生成。','头与双臂依据原始形变区域，腿部采用近似空间蒙皮。','未生成逐指骨骼或原生 MMD IK、布料与刚体物理。'],
        'generated_rig_regions':{name:len(mask) for name,mask in masks.items()},
        'generated_rig_revision':2,'generated_arm_garment_guards':guarded_arm_vertices})


def read_glb(source: Path) -> dict:
    glb=Glb(source);doc=glb.doc;worlds,parents,active=glb.worlds();profile=_profile(source,glb)
    skin_indices={node['skin'] for i,node in enumerate(doc.get('nodes',[])) if i in active and 'mesh' in node and 'skin' in node}
    skins={};joints=set()
    for reference in skin_indices:
        skin=_index(doc.get('skins',[]),reference,'蒙皮索引');ids=skin.get('joints',[])
        if not isinstance(ids,list) or not ids or len(ids)>MAX_BONES or len(set(ids))!=len(ids):raise ValueError('GLB 蒙皮骨骼无效。')
        for joint in ids:_integer(joint,len(worlds)-1,'蒙皮骨骼索引')
        matrices=glb.accessor(skin['inverseBindMatrices'],'MAT4',MAX_BONES) if 'inverseBindMatrices' in skin else [sum(IDENTITY,())]*len(ids)
        if len(matrices)!=len(ids):raise ValueError('GLB 绑定矩阵与骨骼数量不一致。')
        matrices=[tuple(tuple(flat[c*4+r] for c in range(4)) for r in range(4)) for flat in matrices]
        skins[reference]=(ids,[_matrix_product(worlds[j],matrix) for j,matrix in zip(ids,matrices)]);joints.update(ids)
    joint_order=sorted(joints);lookup={j:i for i,j in enumerate(joint_order)};bones=[]
    for j in joint_order:
        parent=parents[j]
        while parent>=0 and parent not in lookup:parent=parents[parent]
        unity=_matrix_product(_matrix_product(REFLECT,worlds[j]),REFLECT)
        bones.append({'name':str(doc['nodes'][j].get('name','Bone'+str(j)))[:1024], 'parent':lookup.get(parent,-1),
                      'position':(unity[0][3],unity[1][3],unity[2][3]), 'rotation':_quaternion(unity)})
    model={'vertices':[],'indices':[],'materials':[],'bones':bones,'morphs':[],
           'metadata':{'rig_source':'authored',**profile}}
    morphs={};morph_count=0
    for node_index,node in enumerate(doc.get('nodes',[])):
        if node_index not in active or 'mesh' not in node:continue
        mesh=_index(doc.get('meshes',[]),node['mesh'],'网格索引');primitives=mesh.get('primitives',[])
        if not isinstance(primitives,list) or len(primitives)>4096:raise ValueError('GLB 网格分组数量超限。')
        ids,binds=skins[node['skin']] if 'skin' in node else ([],[])
        for part in primitives:
            if part.get('mode',4)!=4 or part.get('extensions'):raise ValueError('GLB 需要未压缩的三角面网格。')
            attributes=part.get('attributes',{});positions=glb.accessor(attributes.get('POSITION'),'VEC3',MAX_VERTICES)
            start=len(model['vertices']);count=len(positions)
            if not count or start+count>MAX_VERTICES:raise ValueError('GLB 网格顶点数量超限。')
            normals=glb.accessor(attributes['NORMAL'],'VEC3',MAX_VERTICES) if 'NORMAL' in attributes else [(0.,1.,0.)]*count
            uv=glb.accessor(attributes['TEXCOORD_0'],'VEC2',MAX_VERTICES) if 'TEXCOORD_0' in attributes else [(0.,0.)]*count
            if len(normals)!=count or len(uv)!=count:raise ValueError('GLB 顶点属性数量不一致。')
            if ids:
                if not all(name in attributes for name in ('JOINTS_0','WEIGHTS_0')):raise ValueError('GLB 蒙皮网格缺少骨骼权重。')
                joint_rows=glb.accessor(attributes['JOINTS_0'],'VEC4',MAX_VERTICES);weights=glb.accessor(attributes['WEIGHTS_0'],'VEC4',MAX_VERTICES)
                if len(joint_rows)!=count or len(weights)!=count:raise ValueError('GLB 蒙皮顶点数量不一致。')
            else:joint_rows=[(-1,)*4]*count;weights=[(0.,)*4]*count
            matrices=[]
            for i,pos in enumerate(positions):
                influence=[]
                if ids:
                    for j,w in zip(joint_rows[i],weights[i]):
                        if type(j) is not int or not 0<=j<len(ids) or not 0<=w<=1.0001:raise ValueError('GLB 骨骼索引或权重无效。')
                        if w>0:influence.append((j,w))
                    total=sum(w for j,w in influence)
                    if total<.001 or abs(total-1)>.02:raise ValueError('GLB 骨骼权重未归一化。')
                    influence=[(j,w/total) for j,w in influence]
                    matrix=tuple(tuple(sum(binds[j][r][c]*w for j,w in influence) for c in range(4)) for r in range(4))
                    bone_weights=[(lookup[ids[j]],w) for j,w in influence]
                else:
                    matrix=worlds[node_index];parent=parents[node_index]
                    while parent>=0 and parent not in lookup:parent=parents[parent]
                    bone_weights=[(lookup.get(parent,0),1.)] if bones else []
                matrices.append(matrix);p=_finite(_point(matrix,pos));n=_unit(_normal(matrix,normals[i]));_finite(uv[i])
                bone_weights += [(-1,0.)]*(4-len(bone_weights))
                model['vertices'].append((p[0],p[1],-p[2],n[0],n[1],-n[2],uv[i][0],1-uv[i][1],
                    *(b for b,w in bone_weights),*(w for b,w in bone_weights)))
            raw_indices=[row[0] for row in glb.accessor(part['indices'],'SCALAR',MAX_INDICES)] if 'indices' in part else list(range(count))
            if len(raw_indices)%3 or any(type(i) is not int or not 0<=i<count for i in raw_indices):raise ValueError('GLB 三角面索引无效。')
            if len(model['indices'])+len(raw_indices)>MAX_INDICES:raise ValueError('GLB 三角面数量超限。')
            for i in range(0,len(raw_indices),3):model['indices'].extend((start+raw_indices[i],start+raw_indices[i+2],start+raw_indices[i+1]))
            material=glb.material(part.get('material'),str(node.get('name','Mesh')));material['count']=len(raw_indices);model['materials'].append(material)
            targets=part.get('targets',[]);names=mesh.get('extras',{}).get('targetNames',[])
            if len(targets)>1024:raise ValueError('GLB 形变数量超限。')
            defaults=node.get('weights',mesh.get('weights',[0.]*len(targets)))
            if len(defaults)!=len(targets) or any(type(w) not in (float,int) or not math.isfinite(w) or abs(w)>1e-8 for w in defaults):
                raise ValueError('GLB 请以中性形变姿态导出模型。')
            for k,target in enumerate(targets):
                if 'POSITION' not in target:continue
                offsets=glb.accessor(target['POSITION'],'VEC3',MAX_VERTICES)
                if len(offsets)!=count:raise ValueError('GLB 形变顶点数量不一致。')
                name=str(names[k] if k<len(names) else 'Morph'+str(k))[:1024];rows=morphs.setdefault(name,[])
                for i,offset in enumerate(offsets):
                    if any(abs(v)>1e-10 for v in offset):
                        d=_finite(_direction(matrices[i],offset));rows.append((start+i,d[0],d[1],-d[2]));morph_count+=1
                        if morph_count>MAX_MORPH_VALUES:raise ValueError('GLB 顶点形变资源超限。')
    model['morphs']=[{'name':name,'offsets':rows} for name,rows in morphs.items() if rows]
    if not model['vertices']:raise ValueError('GLB 没有可用角色网格。')
    if not bones:
        if profile.get('rig_profile')!='whale-child':raise ValueError('GLB 没有蒙皮骨骼；只有已知幼年模型支持基础骨架适配。')
        _basic_child_rig(model)
    from runtime_rig_adaptation import adapt_whale_hands
    adapt_whale_hands(model)
    from runtime_body_rig_adaptation import adapt_whale_body
    adapt_whale_body(model)
    start=0;forward=None
    for material in model['materials']:
        indices=set(model['indices'][start:start+material['count']]);start+=material['count']
        name=material['name'].lower()
        if not indices or not any(token in name for token in ('projected_mouth','projected_lips','face','顔','脸')):continue
        n=tuple(sum(model['vertices'][i][k] for i in indices)/len(indices) for k in (3,5));length=math.sqrt(sum(v*v for v in n))
        if length>.15:forward=(n[0]/length,0.,n[1]/length);break
    if forward is not None:model['mesh_forward']=forward;model['orientation_evidence']='GLB projected facial surface normals'
    elif profile:model['mesh_forward']=(0.,0.,-1.);model['orientation_evidence']='known profile authored glTF +Z front'
    validate_rig(model)
    return model
