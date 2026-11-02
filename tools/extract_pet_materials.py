"""Extract material-to-texture metadata from the user's PMX for their existing FBX."""
import json
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
source = ROOT / 'Litamodel' / '丽塔未变身1.0.pmx'
with source.open('rb') as f:
    def read(fmt):
        return struct.unpack('<' + fmt, f.read(struct.calcsize('<' + fmt)))
    assert f.read(4) == b'PMX '
    version, = read('f')
    header = f.read(read('B')[0])
    encoding, uv, vi, ti, mi, bi, moi, ri = header[:8]
    def string():
        return f.read(read('i')[0]).decode('utf-16-le' if encoding == 0 else 'utf-8')
    def index(size):
        return read({1:'b',2:'h',4:'i'}[size])[0]
    for _ in range(4): string()
    vertex_count = read('i')[0]
    for _ in range(vertex_count):
        f.seek(32 + uv * 16, 1)
        mode = read('B')[0]
        f.seek({0:bi, 1:2*bi+4, 2:4*bi+16, 3:2*bi+40, 4:4*bi+16}[mode] + 4, 1)
    f.seek(read('i')[0] * vi, 1)
    textures = [string().replace('\\', '/') for _ in range(read('i')[0])]
    materials = []
    for _ in range(read('i')[0]):
        name, english = string(), string()
        color = list(read('4f'))
        f.seek(12 + 4 + 12, 1)
        flags = read('B')[0]
        f.seek(20, 1)
        texture = index(ti)
        index(ti)
        read('B')
        if read('B')[0]: read('B')
        else: index(ti)
        string()
        indices = read('i')[0]
        materials.append({'name': name, 'texture': Path(textures[texture]).name if texture >= 0 else '', 'color': color, 'doubleSided': bool(flags & 1), 'indexCount':indices})
    result = {'source': source.name, 'vertices': vertex_count, 'materials': materials}
    out = ROOT / 'pet-frontend/pet-frontend/Assets/Models/Lita/PetMaterialMap.json'
    out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf8')
    print(json.dumps(result, ensure_ascii=True))
