"""Portable PMX / FBX / GLB conversion. No Editor, Blender, or source-file writes.

PETRAW1 stores a bind-pose mesh, skeleton, materials and sparse vertex morphs.
Unity creates its own runtime meshes and retargets the bundled animation rig.
"""
from __future__ import annotations

from array import array
import hashlib
import math
from pathlib import Path
import shutil
import struct
import zlib

from errors import ServiceError

MAX_VERTICES = 600_000
MAX_INDICES = 3_000_000
MAX_BONES = 4096
MAX_MORPH_VALUES = 4_000_000


class Reader:
    def __init__(self, stream): self.stream = stream
    def bytes(self, size):
        if size < 0 or size > 256 * 1024 * 1024: raise ValueError("模型字段长度超限。")
        value = self.stream.read(size)
        if len(value) != size: raise ValueError("模型文件不完整。")
        return value
    def read(self, fmt): return struct.unpack("<" + fmt, self.bytes(struct.calcsize("<" + fmt)))
    def count(self, maximum):
        value, = self.read("i")
        if not 0 <= value <= maximum: raise ValueError("模型资源数量超限。")
        return value
    def index(self, size, unsigned=False): return self.read(({1:"B",2:"H",4:"I"} if unsigned else {1:"b",2:"h",4:"i"})[size])[0]
    def text(self, encoding): return self.bytes(self.count(1024 * 1024)).decode(encoding, errors="replace")


def _finite(values):
    if not all(math.isfinite(value) and abs(value) < 1e8 for value in values):
        raise ValueError("模型含有非法坐标或权重。")
    return values


def read_pmx(source: Path) -> dict:
    with source.open("rb") as stream:
        r = Reader(stream)
        if r.bytes(4) != b"PMX ": raise ValueError("不是有效的 PMX 模型。")
        version, = r.read("f")
        if not 1.99 <= version <= 2.11: raise ValueError("支持 PMX 2.0 / 2.1 模型。")
        header = r.bytes(r.read("B")[0])
        if len(header) < 8: raise ValueError("PMX 文件头不完整。")
        enc, extra_uv, vi, ti, mi, bi, moi, ri = header[:8]
        if enc not in (0, 1) or extra_uv > 4 or any(n not in (1, 2, 4) for n in (vi,ti,mi,bi,moi,ri)):
            raise ValueError("PMX 文件头无效。")
        encoding = "utf-16-le" if enc == 0 else "utf-8"
        text = lambda: r.text(encoding)
        for _ in range(4): text()
        vertices = []
        for _ in range(r.count(MAX_VERTICES)):
            pos = _finite(r.read("3f")); normal = _finite(r.read("3f")); uv = _finite(r.read("2f"))
            r.bytes(extra_uv * 16)
            mode = r.read("B")[0]
            if mode == 0: bones, weights = [r.index(bi)], [1.0]
            elif mode in (1, 3):
                bones = [r.index(bi), r.index(bi)]; w = r.read("f")[0]; weights = [w, 1-w]
                if mode == 3: r.bytes(36)
            elif mode in (2, 4):
                bones = [r.index(bi) for _ in range(4)]; weights = list(r.read("4f"))
            else: raise ValueError("不支持的 PMX 蒙皮类型。")
            r.read("f")  # edge scale
            _finite(weights)
            bones += [-1] * (4-len(bones)); weights += [0.0] * (4-len(weights))
            vertices.append((pos[0],pos[1],-pos[2],normal[0],normal[1],-normal[2],uv[0],1-uv[1],*bones,*weights))
        indices = [r.index(vi, unsigned=True) for _ in range(r.count(MAX_INDICES))]
        if len(indices) % 3 or any(i >= len(vertices) for i in indices): raise ValueError("模型三角面索引无效。")
        for i in range(0,len(indices),3): indices[i+1],indices[i+2] = indices[i+2],indices[i+1]
        textures = [text().replace("\\", "/") for _ in range(r.count(4096))]
        materials = []
        for _ in range(r.count(4096)):
            name, _ = text(), text(); color = _finite(r.read("4f"))
            r.bytes(28); flags = r.read("B")[0]; r.bytes(20)
            texture = r.index(ti); r.index(ti); r.read("B")
            if r.read("B")[0]: r.read("B")
            else: r.index(ti)
            text(); count = r.count(MAX_INDICES)
            if count % 3: raise ValueError("PMX 材质面索引无效。")
            materials.append({"name": name, "color": color, "double": bool(flags & 1),
                              "texture": textures[texture] if 0 <= texture < len(textures) else "", "count": count})
        if sum(m["count"] for m in materials) != len(indices): raise ValueError("PMX 材质与三角面数量不一致。")
        bones = []
        for index in range(r.count(MAX_BONES)):
            name, english = text(), text(); pos = _finite(r.read("3f")); parent = r.index(bi)
            r.read("i"); flags = r.read("H")[0]
            if flags & 1: r.index(bi)
            else: r.bytes(12)
            if flags & (0x100 | 0x200): r.index(bi); r.read("f")
            if flags & 0x400: r.bytes(12)
            if flags & 0x800: r.bytes(24)
            if flags & 0x2000: r.read("i")
            if flags & 0x20:
                r.index(bi); r.read("i"); r.read("f")
                for _ in range(r.count(MAX_BONES)):
                    r.index(bi)
                    if r.read("B")[0]: r.bytes(24)
            bones.append({"name": name or english or ("Bone"+str(index)), "parent": parent,
                          "position": (pos[0],pos[1],-pos[2]), "rotation": (0,0,0,1)})
        morphs = []; total_values = 0
        for _ in range(r.count(1024)):
            name, english = text(), text(); r.read("B"); kind = r.read("B")[0]; count = r.count(MAX_MORPH_VALUES)
            total_values += count
            if total_values > MAX_MORPH_VALUES: raise ValueError("模型表情资源过大。")
            offsets = []
            for _ in range(count):
                if kind in (0,9): r.index(moi); r.read("f")
                elif kind == 1:
                    vertex = r.index(vi, unsigned=True); xyz = _finite(r.read("3f"))
                    if vertex >= len(vertices): raise ValueError("PMX 表情顶点索引无效。")
                    offsets.append((vertex, xyz[0], xyz[1], -xyz[2]))
                elif kind == 2: r.index(bi); r.bytes(28)
                elif 3 <= kind <= 7: r.index(vi, unsigned=True); r.bytes(16)
                elif kind == 8: r.index(mi); r.bytes(113)
                elif kind == 10: r.index(ri); r.bytes(25)
                else: raise ValueError("不支持的 PMX 表情格式。")
            if offsets: morphs.append({"name": name or english, "offsets": offsets})
        result = {"vertices": vertices, "indices": indices, "materials": materials, "bones": bones, "morphs": morphs}
        validate_rig(result)
        return result


def validate_rig(model):
    bones = model["bones"]
    if not bones: raise ValueError("模型没有可用的人形骨骼，不能将附件作为角色导入。")
    for i,bone in enumerate(bones):
        parent = bone["parent"]
        if parent < -1 or parent >= len(bones) or parent == i: raise ValueError("模型骨骼父级无效。")
        visited = {i}
        while parent >= 0:
            if parent in visited: raise ValueError("模型骨骼层级形成循环。")
            visited.add(parent); parent = bones[parent]["parent"]
    for vertex in model["vertices"]:
        total = 0
        for b,w in zip(vertex[8:12], vertex[12:16]):
            if w > 0 and not 0 <= b < len(bones): raise ValueError("模型蒙皮引用了不存在的骨骼。")
            if w < -0.0001 or w > 1.0001: raise ValueError("模型蒙皮权重无效。")
            total += w
        if total < 0.001 or abs(total-1) > 0.02: raise ValueError("模型蒙皮权重未归一化。")


def _matrix_product(a,b): return tuple(tuple(sum(a[i][k]*b[k][j] for k in range(4)) for j in range(4)) for i in range(4))
def _point(m,p): return tuple(sum(m[i][k]*p[k] for k in range(3))+m[i][3] for i in range(3))
def _normal(m,p):
    xyz = tuple(sum(m[i][k]*p[k] for k in range(3)) for i in range(3))
    length = math.sqrt(sum(v*v for v in xyz)) or 1
    return tuple(v/length for v in xyz)
def _quaternion(m):
    # Rotation from the normalized columns of the bind-pose world matrix.
    cols = [math.sqrt(sum(m[r][c]**2 for r in range(3))) or 1 for c in range(3)]
    a = [[m[r][c]/cols[c] for c in range(3)] for r in range(3)]
    trace = sum(a[i][i] for i in range(3))
    if trace > 0:
        s = math.sqrt(trace+1)*2; return ((a[2][1]-a[1][2])/s,(a[0][2]-a[2][0])/s,(a[1][0]-a[0][1])/s,s/4)
    i = max(range(3),key=lambda i:a[i][i]); j,k = (i+1)%3,(i+2)%3
    s = math.sqrt(max(1+a[i][i]-a[j][j]-a[k][k],1e-12))*2
    q = [0.,0.,0.,0.]; q[i] = s/4; q[j] = (a[i][j]+a[j][i])/s; q[k] = (a[i][k]+a[k][i])/s; q[3] = (a[k][j]-a[j][k])/s
    return tuple(q)


def read_fbx(source: Path) -> dict:
    try: import assimp_py as ai
    except ImportError: raise ServiceError("发行包缺少 FBX 导入组件，请使用完整发行包或导入 PMX/PETMODEL。", 409) from None
    scene = ai.import_file(str(source), ai.Process_Triangulate | ai.Process_LimitBoneWeights | ai.Process_GenSmoothNormals)
    nodes = []; mesh_nodes = {}; node_lookup = {}
    identity = ((1,0,0,0),(0,1,0,0),(0,0,1,0),(0,0,0,1))
    reflect = ((1,0,0,0),(0,1,0,0),(0,0,-1,0),(0,0,0,1))
    def visit(node,parent,world):
        index = len(nodes)
        if index >= MAX_BONES: raise ValueError("FBX 骨骼数量超限。")
        world = _matrix_product(world,node.transformation)
        unity = _matrix_product(_matrix_product(reflect,world),reflect)
        nodes.append({"name":node.name,"parent":parent,"position":(unity[0][3],unity[1][3],unity[2][3]),"rotation":_quaternion(unity),"matrix":world})
        node_lookup[node.name] = index
        for mesh_index in node.mesh_indices: mesh_nodes[mesh_index] = world
        for child in node.children: visit(child,index,world)
    visit(scene.root_node,-1,identity)
    vertices = []; indices = []; materials = []; mapped_bones = set()
    for mesh_index,mesh in enumerate(scene.meshes):
        if not mesh.bones: continue  # Ignore imported cameras, rigid-body debug geometry.
        if len(vertices)+mesh.num_vertices > MAX_VERTICES or len(indices)+mesh.num_indices > MAX_INDICES:
            raise ValueError("FBX 网格数量超限。")
        world = mesh_nodes.get(mesh_index,identity); start = len(vertices)
        weights = [[] for _ in range(mesh.num_vertices)]
        for bone in mesh.bones:
            if not bone.num_weights: continue
            if bone.name not in node_lookup: raise ValueError("FBX 骨骼与蒙皮名称不匹配。")
            bone_index = node_lookup[bone.name]; mapped_bones.add(bone_index)
            for vertex,weight in zip(bone.weight_vertex_ids,bone.weights):
                if vertex >= mesh.num_vertices: raise ValueError("FBX 蒙皮顶点无效。")
                if weight > 0: weights[vertex].append((bone_index,weight))
        uv = mesh.texcoords[0] if mesh.texcoords else None
        uv_size = mesh.num_uv_components[0] if mesh.num_uv_components else 2
        for i in range(mesh.num_vertices):
            pos = _finite(_point(world,mesh.vertices[3*i:3*i+3])); normal = _normal(world,mesh.normals[3*i:3*i+3])
            uv_pair = (uv[uv_size*i],uv[uv_size*i+1]) if uv is not None else (0,0)
            influence = sorted(weights[i],key=lambda item:item[1],reverse=True)[:4]
            if not influence: raise ValueError("FBX 蒙皮存在没有骨骼权重的顶点。")
            total = sum(w for _,w in influence); influence += [(-1,0)]*(4-len(influence))
            vertices.append((pos[0],pos[1],-pos[2],normal[0],normal[1],-normal[2],*uv_pair,*(b for b,w in influence),*(w/total for b,w in influence)))
        raw_indices = list(mesh.indices)
        if len(raw_indices)%3: raise ValueError("FBX 网格未能三角化。")
        for i in range(0,len(raw_indices),3): indices.extend((start+raw_indices[i],start+raw_indices[i+2],start+raw_indices[i+1]))
        material = scene.materials[mesh.material_index]
        color = list(material.get("COLOR_DIFFUSE",(1,1,1)))[:3] + [float(material.get("OPACITY",1))]
        texture_entries = material.get("TEXTURES",{}).get(ai.TextureType_DIFFUSE,[])
        texture = texture_entries[0] if texture_entries else ""
        materials.append({"name":str(material.get("NAME",mesh.name)),"color":color,"texture":texture,"double":True,"count":len(raw_indices)})
    if not mapped_bones or not vertices: raise ValueError("FBX 没有蒙皮人形角色，刚性附件不能独立导入。")
    result = {"vertices":vertices,"indices":indices,"materials":materials,"bones":nodes,"morphs":[]}
    validate_rig(result)
    return result


def _png(width,height,pixels):
    def chunk(kind,payload): return struct.pack(">I",len(payload))+kind+payload+struct.pack(">I",zlib.crc32(kind+payload)&0xffffffff)
    scanlines = b"".join(b"\0"+pixels[y*width*4:(y+1)*width*4] for y in range(height))
    return b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",width,height,8,6,0,0,0))+chunk(b"IDAT",zlib.compress(scanlines))+chunk(b"IEND",b"")


def _tga_png(source):
    raw = source.read_bytes()
    if len(raw)<18: raise ValueError("TGA 贴图不完整。")
    id_size,cmap,kind,_,_,_,_,_,width,height,depth,descriptor = struct.unpack("<BBBHHBHHHHBB",raw[:18])
    if cmap or kind not in (2,3,10,11) or depth not in (8,24,32) or not 0<width*height<=16_777_216:
        raise ValueError("TGA 贴图格式暂不支持，请改用 PNG / JPG。")
    size = depth//8; at = 18+id_size; pixels = []
    while len(pixels)<width*height:
        count, repeated = 1, False
        if kind in (10,11):
            if at>=len(raw): raise ValueError("TGA 贴图不完整。")
            packet=raw[at];at+=1;count=(packet&127)+1;repeated=bool(packet&128)
        for i in range(1 if repeated else count):
            value=raw[at:at+size];at+=size
            if len(value)!=size: raise ValueError("TGA 贴图不完整。")
            rgba=bytes((value[2],value[1],value[0],value[3] if size==4 else 255)) if size>=3 else bytes((value[0],value[0],value[0],255))
            pixels.extend([rgba]*count if repeated else [rgba])
        if len(pixels)>width*height: raise ValueError("TGA 贴图游程无效。")
    if not descriptor&32: pixels=[v for y in reversed(range(height)) for v in pixels[y*width:(y+1)*width]]
    if descriptor&16: pixels=[v for y in range(height) for v in reversed(pixels[y*width:(y+1)*width])]
    return _png(width,height,b"".join(pixels))


def _copy_texture(source_dir,reference,output):
    if not reference: return ""
    relative = Path(str(reference).replace("\\","/"))
    if relative.is_absolute(): relative=Path(relative.name)  # FBX may retain the creator's machine path; resolve only the local basename.
    if ".." in relative.parts: raise ValueError("贴图必须保存在模型目录内，不能引用其他目录。")
    root=source_dir.resolve(); source=(root/relative).resolve()
    if not source.is_relative_to(root): raise ValueError("贴图路径越过模型目录。")
    if not source.is_file():
        source=(root/relative.name).resolve()
        if not source.is_relative_to(root): raise ValueError("贴图路径越过模型目录。")
        if not source.is_file(): raise ValueError("找不到模型贴图："+relative.name)
    if source.stat().st_size>64*1024*1024: raise ValueError("单张贴图不能超过 64 MB。")
    suffix=source.suffix.lower()
    if suffix not in (".png",".jpg",".jpeg",".tga"): raise ValueError("贴图支持 PNG、JPG、TGA，请转换："+source.name)
    raw=source.read_bytes()
    if suffix==".png":
        if len(raw)<24 or raw[:8]!=b"\x89PNG\r\n\x1a\n": raise ValueError("PNG 贴图文件无效："+source.name)
        width,height=struct.unpack(">II",raw[16:24])
        if not 0<width*height<=16_777_216: raise ValueError("贴图分辨率过大，最多 1600 万像素："+source.name)
    name=hashlib.sha256(raw).hexdigest()[:24]+(".png" if suffix==".tga" else suffix)
    target=output/"textures"/name;target.parent.mkdir(parents=True,exist_ok=True)
    if not target.exists():
        if suffix==".tga":target.write_bytes(_tga_png(source))
        else:shutil.copy2(source,target)
    return "textures/"+name


def _store_embedded_texture(reference,output):
    raw=reference['data'];suffix=reference['suffix']
    if len(raw)>64*1024*1024 or suffix not in ('.png','.jpg','.jpeg'):
        raise ValueError('GLB 内嵌贴图格式无效或超过 64 MB。')
    if suffix=='.png':
        if len(raw)<24 or raw[:8]!=b'\x89PNG\r\n\x1a\n':raise ValueError('GLB 内嵌 PNG 贴图无效。')
        width,height=struct.unpack('>II',raw[16:24])
        if not width or not height or width*height>16_777_216:raise ValueError('GLB 内嵌贴图分辨率超过 1600 万像素。')
    elif len(raw)<3 or raw[:3]!=b'\xff\xd8\xff':raise ValueError('GLB 内嵌 JPG 贴图无效。')
    name=hashlib.sha256(raw).hexdigest()[:24]+suffix
    target=output/'textures'/name;target.parent.mkdir(parents=True,exist_ok=True)
    if not target.exists():target.write_bytes(raw)
    return 'textures/'+name


def convert_model(source: Path, output: Path) -> dict:
    suffix=source.suffix.lower()
    if suffix=='.pmx':model=read_pmx(source)
    elif suffix=='.fbx':model=read_fbx(source)
    elif suffix=='.glb':
        from runtime_glb import read_glb
        model=read_glb(source)
    else:raise ValueError('请选择 PMX、FBX 或 GLB 模型。')
    if output.resolve()==source.parent.resolve():raise ValueError('模型缓存必须写入独立目录，不能覆盖源模型目录。')
    output.mkdir(parents=True,exist_ok=True)
    for material in model['materials']:
        reference=material['texture']
        material['texture']=_store_embedded_texture(reference,output) if isinstance(reference,dict) else _copy_texture(source.parent,reference,output)
    target=output/"model.petraw";temporary=target.with_suffix(".tmp")
    with temporary.open("wb") as stream:
        def write(fmt,*values):stream.write(struct.pack("<"+fmt,*values))
        def text(value):raw=value.encode("utf-8");write("i",len(raw));stream.write(raw)
        stream.write(b"PETRAW1\0");write("i",len(model["vertices"]))
        for vertex in model["vertices"]:write("8f4i4f",*vertex)
        write("i",len(model["indices"]));stream.write(array("i",model["indices"]).tobytes())
        write("i",len(model["materials"]))
        for m in model["materials"]:text(m["name"]);write("4fB",*m["color"],int(m["double"]));text(m["texture"]);write("i",m["count"])
        write("i",len(model["bones"]))
        for b in model["bones"]:text(b["name"]);write("i3f4f",b["parent"],*b["position"],*b["rotation"])
        write("i",len(model["morphs"]))
        for m in model["morphs"]:
            text(m["name"]);write("i",len(m["offsets"]))
            for offset in m["offsets"]:write("i3f",*offset)
    temporary.replace(target)
    # Record a mesh-space front from authored facial normals, independent of
    # inconsistent left/right bone labels in mirrored MMD exports.
    start=0; forward=model.get('mesh_forward',(0.0,0.0,0.0))
    for material in model["materials"]:
        if forward!=(0.0,0.0,0.0):break
        selected=set(model["indices"][start:start+material["count"]]);start+=material["count"]
        name=material["name"].lower()
        if not selected or not ("face" in name or "顔" in name or "脸" in name):continue
        x=sum(model["vertices"][i][3] for i in selected)/len(selected)
        z=sum(model["vertices"][i][5] for i in selected)/len(selected)
        length=math.sqrt(x*x+z*z)
        if length>0.15:forward=(x/length,0.0,z/length);break
    import json
    orientation={"version":1,"source_type":source.suffix.lower()[1:],"coordinate_system":"unity-left-handed","mesh_forward":forward,
                 "evidence":model.get('orientation_evidence',"facial-material surface normals" if forward!=(0.0,0.0,0.0) else "unknown; use skeleton fallback"),
                 **{key:value for key,value in model.get('metadata',{}).items() if key!='display_name'}}
    orientation_file=output/"orientation.json"
    orientation_file.write_text(json.dumps(orientation,ensure_ascii=False,indent=2),encoding="utf-8")
    return {"vertices":len(model["vertices"]),"bones":len(model["bones"]),"materials":len(model["materials"]),"morphs":len(model["morphs"]),
            **model.get('metadata',{})}


def main():
    import argparse
    import json
    import sys
    parser=argparse.ArgumentParser(description="Portable desktop pet model conversion")
    parser.add_argument("--source",type=Path,required=True);parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    try:metrics=convert_model(args.source,args.output)
    except Exception as error:
        args.output.mkdir(parents=True,exist_ok=True)
        args.output.joinpath("conversion-metrics.json").write_text(json.dumps({"error":str(error)[:1200]},ensure_ascii=False),encoding="utf-8")
        sys.exit(2)
    args.output.joinpath("conversion-metrics.json").write_text(json.dumps(metrics,ensure_ascii=False),encoding="utf-8")


if __name__=="__main__":main()
