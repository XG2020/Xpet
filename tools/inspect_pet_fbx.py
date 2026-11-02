"""Read the existing Blender-exported FBX without importing native FBX libraries."""
import struct
import sys
sys.stdout.reconfigure(encoding='utf-8')

data = open(sys.argv[1], 'rb').read()
version = struct.unpack_from('<I', data, 23)[0]
wide = version >= 7500
header = '<QQQB' if wide else '<IIIB'
head_size = struct.calcsize(header)

def node(pos, depth=0):
    end, count, prop_len, name_len = struct.unpack_from(header, data, pos)
    if not end:
        return len(data)
    start = pos + head_size
    name = data[start:start+name_len].decode('utf8')
    p = start + name_len
    vals = []
    formats = {'Y': '<h', 'C': '?', 'I': '<i', 'F': '<f', 'D': '<d', 'L': '<q'}
    for _ in range(count):
        kind = chr(data[p]); p += 1
        if kind in formats:
            fmt = formats[kind]
            vals.append(struct.unpack_from(fmt, data, p)[0]); p += struct.calcsize(fmt)
        elif kind in ('S', 'R'):
            length = struct.unpack_from('<I', data, p)[0]; p += 4
            raw = data[p:p+length]; p += length
            vals.append(raw.decode('utf8', errors='replace') if kind == 'S' else f'<{length} bytes>')
        elif kind in ('f', 'd', 'l', 'i', 'b', 'c'):
            length, encoding, compressed = struct.unpack_from('<III', data, p); p += 12
            p += compressed
            vals.append(f'<array {length}>')
        else:
            raise ValueError(kind)
    main_bones = {'上半身', '上半身2', '頭', '首', '腕.R', '腕.L', 'ひじ.R', 'ひじ.L', '手首.R', '手首.L'}
    main_shapes = {'まばたき', 'あ', '笑い'}
    if name in ('Material', 'Texture', 'Video', 'FileName', 'RelativeFilename', 'AnimationStack') or (name == 'Model' and vals[-1] == 'LimbNode' and vals[1].split('\0')[0] in main_bones) or (name == 'Deformer' and vals[-1] == 'BlendShapeChannel' and vals[1].split('\0')[0] in main_shapes):
        print('  '*depth + name, repr(vals))
    elif '--shapes' in sys.argv and name == 'Deformer' and vals[-1] == 'BlendShapeChannel':
        print('Shape', repr(vals[1].split('\0')[0]))
    while p + head_size < end:
        p = node(p, depth+1)
    return end

p=27
while p + head_size < len(data):
    p=node(p)
