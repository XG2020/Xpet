"""Read-only geometry audit for PMX/GLB desktop-pet sources.

This CLI reads source meshes and writes an optional JSON report. It does not
convert a model, rewrite a source, contact a service, or invoke Unity/Blender.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import struct
import sys


def bounds(points):
    points = list(points)
    if not points:
        return None
    return {"min": [min(p[i] for p in points) for i in range(3)],
            "max": [max(p[i] for p in points) for i in range(3)]}


def delta_report(name, offsets, material_vertices, vertices):
    active = {i: d for i, d in offsets.items() if sum(x*x for x in d) > 1e-14}
    hits = []
    for material_name, indices in material_vertices:
        selected = indices & active.keys()
        if not selected:
            continue
        hits.append({"material": material_name, "nonzero_vertices": len(selected),
                     "affected_bounds": bounds(vertices[i] for i in selected),
                     "max_delta": max(math.sqrt(sum(x*x for x in active[i])) for i in selected)})
    return {"name": name, "nonzero_vertices": len(active), "materials": hits}


def read_pmx(source):
    backend = Path(__file__).resolve().parents[1] / "pet-backend"
    sys.path.insert(0, str(backend))
    spec = importlib.util.spec_from_file_location("pet_geometry_runtime_models", backend / "runtime_models.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    model = module.read_pmx(source)
    vertices = [v[:3] for v in model["vertices"]]
    materials, material_vertices, start = [], [], 0
    for material in model["materials"]:
        selected = set(model["indices"][start:start+material["count"]])
        start += material["count"]
        material_vertices.append((material["name"], selected))
        materials.append({"name": material["name"], "vertices": len(selected),
                          "alpha": material["color"][3], "bounds": bounds(vertices[i] for i in selected)})
    morphs = [delta_report(m["name"], {d[0]: d[1:] for d in m["offsets"]}, material_vertices, vertices)
              for m in model["morphs"]]
    return {"source_type": "pmx", "coordinate_system": "unity-left-handed",
            "vertices": len(vertices), "bounds": bounds(vertices), "bones": model["bones"],
            "materials": materials, "morphs": morphs}


def product(a, b):
    return tuple(tuple(sum(a[r][k]*b[k][c] for k in range(4)) for c in range(4)) for r in range(4))


def node_matrix(node):
    if "matrix" in node:
        return tuple(tuple(node["matrix"][c*4+r] for c in range(4)) for r in range(4))
    x, y, z, w = node.get("rotation", [0, 0, 0, 1])
    sx, sy, sz = node.get("scale", [1, 1, 1])
    tx, ty, tz = node.get("translation", [0, 0, 0])
    return ((sx*(1-2*y*y-2*z*z), sy*(2*x*y-2*z*w), sz*(2*x*z+2*y*w), tx),
            (sx*(2*x*y+2*z*w), sy*(1-2*x*x-2*z*z), sz*(2*y*z-2*x*w), ty),
            (sx*(2*x*z-2*y*w), sy*(2*y*z+2*x*w), sz*(1-2*x*x-2*y*y), tz),
            (0, 0, 0, 1))


def point(matrix, value, vector=False):
    return tuple(sum(matrix[r][c]*value[c] for c in range(3)) + (0 if vector else matrix[r][3]) for r in range(3))


def read_glb(source):
    raw = source.read_bytes()
    magic, version, length = struct.unpack_from("<III", raw)
    if magic != 0x46546c67 or version != 2 or length != len(raw):
        raise ValueError("Invalid GLB 2.0 header")
    document, binary, offset = None, b"", 12
    while offset < length:
        size, kind = struct.unpack_from("<II", raw, offset)
        value = raw[offset+8:offset+8+size]
        if len(value) != size:
            raise ValueError("Incomplete GLB chunk")
        if kind == 0x4e4f534a:
            document = json.loads(value)
        elif kind == 0x004e4942:
            binary = value
        offset += size+8
    if document is None:
        raise ValueError("Missing GLB JSON")
    component = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
    widths = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
    def view(index):
        v = document["bufferViews"][index]
        if v.get("buffer", 0) != 0:
            raise ValueError("Audit expects embedded GLB buffer")
        return v, v.get("byteOffset", 0)
    def accessor(index):
        a = document["accessors"][index]
        fmt, size = component[a["componentType"]]
        width = widths[a["type"]]
        values = [(0,)*width for _ in range(a["count"])]
        if "bufferView" in a:
            v, start = view(a["bufferView"])
            start += a.get("byteOffset", 0)
            stride = v.get("byteStride", size*width)
            values = [struct.unpack_from("<"+fmt*width, binary, start+i*stride) for i in range(a["count"])]
        if "sparse" in a:
            sparse = a["sparse"]
            indices, sparse_values = sparse["indices"], sparse["values"]
            _, at = view(indices["bufferView"])
            at += indices.get("byteOffset", 0)
            index_fmt, index_size = component[indices["componentType"]]
            _, values_at = view(sparse_values["bufferView"])
            values_at += sparse_values.get("byteOffset", 0)
            for i in range(sparse["count"]):
                target = struct.unpack_from("<"+index_fmt, binary, at+i*index_size)[0]
                values[target] = struct.unpack_from("<"+fmt*width, binary, values_at+i*size*width)
        return values
    identity = ((1,0,0,0), (0,1,0,0), (0,0,1,0), (0,0,0,1))
    nodes = document.get("nodes", [])
    parents = {child: i for i, n in enumerate(nodes) for child in n.get("children", [])}
    worlds = {}
    def world(index):
        if index not in worlds:
            worlds[index] = product(world(parents[index]) if index in parents else identity, node_matrix(nodes[index]))
        return worlds[index]
    materials, morphs, all_vertices = [], [], []
    for node_index, node in enumerate(nodes):
        if "mesh" not in node:
            continue
        mesh = document["meshes"][node["mesh"]]
        target_names = mesh.get("extras", {}).get("targetNames", [])
        for primitive_index, primitive in enumerate(mesh["primitives"]):
            local = accessor(primitive["attributes"]["POSITION"])
            vertices = [point(world(node_index), p) for p in local]
            selected = {v[0] for v in accessor(primitive["indices"])} if "indices" in primitive else set(range(len(local)))
            material = document.get("materials", [])[primitive["material"]] if "material" in primitive else {}
            material_name = material.get("name", "material_"+str(primitive.get("material", -1)))
            all_vertices.extend(vertices[i] for i in selected)
            materials.append({"name": material_name, "node": node.get("name", str(node_index)),
                              "primitive": primitive_index, "vertices": len(selected),
                              "bounds": bounds(vertices[i] for i in selected)})
            for i, target in enumerate(primitive.get("targets", [])):
                if "POSITION" not in target:
                    continue
                deltas = accessor(target["POSITION"])
                offsets = {j: point(world(node_index), delta, True) for j, delta in enumerate(deltas)}
                report = delta_report(target_names[i] if i < len(target_names) else "target_"+str(i), offsets, [(material_name, selected)], vertices)
                report["node"] = node.get("name", str(node_index))
                morphs.append(report)
    joints = sorted({joint for skin in document.get("skins", []) for joint in skin["joints"]})
    bones = [{"index": i, "name": nodes[i].get("name", str(i)), "parent": parents.get(i, -1),
              "position": [world(i)[r][3] for r in range(3)]} for i in joints]
    return {"source_type": "glb", "coordinate_system": "gltf-right-handed-meters",
            "bounds_space": "mesh node world transforms before skinning; inverse-bind matrices are not sampled",
            "vertices": len(all_vertices), "bounds": bounds(all_vertices), "bones": bones,
            "nodes": [{"index": i, "name": n.get("name", str(i)), "mesh": n.get("mesh"), "skin": n.get("skin")}
                      for i, n in enumerate(nodes)], "materials": materials, "morphs": morphs}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sources", nargs="+", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if any(source.suffix.lower() not in (".pmx", ".glb") for source in args.sources):
        parser.error("sources must be .pmx or .glb")
    if args.output and args.output.suffix.lower() != ".json":
        parser.error("report output must be .json")
    if args.output and args.output.resolve() in [source.resolve() for source in args.sources]:
        parser.error("report output cannot replace a source model")
    reports = []
    for source in args.sources:
        before = hashlib.sha256(source.read_bytes()).hexdigest()
        report = read_pmx(source) if source.suffix.lower() == ".pmx" else read_glb(source)
        if hashlib.sha256(source.read_bytes()).hexdigest() != before:
            raise RuntimeError("Source changed during geometry audit")
        report.update(source=str(source.resolve()), sha256=before)
        reports.append(report)
    result = json.dumps(reports, ensure_ascii=False, indent=2)
    if args.output:
        output = args.output.resolve()
        output.write_text(result, encoding="utf-8")
        print(str(output))
    else:
        print(result)


if __name__ == "__main__":
    main()
