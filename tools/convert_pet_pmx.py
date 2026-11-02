"""Convert a PMX copy into FBX and PNG material metadata using local Blender.

Run with Blender 3.6 (the script never installs an add-on globally)::

    blender --background --python-exit-code 1 --python tools/convert_pet_pmx.py -- \
        --source character.pmx --output staging/model.fbx \
        --mmd-tools-zip path/to/mmd_tools-v2.10.3.zip

The adjacent PetMaterialMap.json records PMX material slots, including duplicate
names and initially invisible materials. Source models and textures are read only.
"""

import argparse
import hashlib
import importlib
import json
import math
import os
from pathlib import Path
import shutil
import struct
import sys
import zipfile


WORKSPACE = Path(__file__).resolve().parents[1]
MAX_FILE_BYTES = 512 * 1024 * 1024


class PmxReader:
    """Read the material section without executing or interpreting model text."""

    def __init__(self, path):
        if path.stat().st_size > MAX_FILE_BYTES:
            raise ValueError("PMX exceeds the 512 MiB import limit.")
        self.file = path.open("rb")

    def close(self):
        self.file.close()

    def exact(self, length):
        if length < 0:
            raise ValueError("Invalid negative PMX length.")
        value = self.file.read(length)
        if len(value) != length:
            raise ValueError("Truncated PMX file.")
        return value

    def unpack(self, fmt):
        return struct.unpack("<" + fmt, self.exact(struct.calcsize("<" + fmt)))

    def count(self, maximum):
        value = self.unpack("i")[0]
        if value < 0 or value > maximum:
            raise ValueError("PMX count exceeds the supported import limit.")
        return value

    def text(self):
        return self.exact(self.count(1024 * 1024)).decode(self.encoding)

    def index(self, size):
        return self.unpack({1: "b", 2: "h", 4: "i"}[size])[0]


def read_materials(source):
    reader = PmxReader(source)
    try:
        if reader.exact(4) != b"PMX ":
            raise ValueError("The source is not a PMX file.")
        version = reader.unpack("f")[0]
        if not (abs(version - 2.0) < 0.01 or abs(version - 2.1) < 0.01):
            raise ValueError("Only PMX 2.0 and 2.1 are supported.")
        header = reader.exact(reader.unpack("B")[0])
        if len(header) < 8 or header[0] not in (0, 1) or header[1] > 4:
            raise ValueError("Invalid PMX header.")
        encoding, extra_uv, vertex_size, texture_size, _, bone_size, _, _ = header[:8]
        if any(size not in (1, 2, 4) for size in header[2:8]):
            raise ValueError("Invalid PMX index size.")
        reader.encoding = "utf-16-le" if encoding == 0 else "utf-8"
        for _ in range(4):
            reader.text()  # Names/comments are data, never instructions.
        vertex_count = reader.count(2_000_000)
        skin_bytes = {
            0: bone_size,
            1: 2 * bone_size + 4,
            2: 4 * bone_size + 16,
            3: 2 * bone_size + 40,
            4: 4 * bone_size + 16,
        }
        for _ in range(vertex_count):
            reader.exact(32 + extra_uv * 16)
            kind = reader.unpack("B")[0]
            if kind not in skin_bytes:
                raise ValueError("Unsupported PMX vertex skinning mode.")
            reader.exact(skin_bytes[kind] + 4)
        index_count = reader.count(12_000_000)
        if index_count % 3:
            raise ValueError("PMX triangle index count is invalid.")
        reader.exact(index_count * vertex_size)
        textures = [reader.text().replace("\\", "/") for _ in range(reader.count(16384))]

        def texture(index):
            if index == -1:
                return ""
            if index < 0 or index >= len(textures):
                raise ValueError("PMX material refers to a missing texture index.")
            return textures[index]

        materials = []
        used_indices = 0
        for index in range(reader.count(4096)):
            name, _ = reader.text(), reader.text()
            color = list(reader.unpack("4f"))
            if not all(math.isfinite(value) for value in color):
                raise ValueError("PMX material contains non-finite colors.")
            reader.exact(28)
            flags = reader.unpack("B")[0]
            reader.exact(20)
            diffuse = texture(reader.index(texture_size))
            sphere = texture(reader.index(texture_size))
            sphere_mode = reader.unpack("B")[0]
            shared_toon = reader.unpack("B")[0]
            if shared_toon not in (0, 1):
                raise ValueError("Invalid PMX toon texture mode.")
            toon = "" if shared_toon else texture(reader.index(texture_size))
            if shared_toon:
                reader.unpack("B")
            reader.text()
            material_indices = reader.count(index_count)
            if material_indices % 3:
                raise ValueError("PMX material triangle count is invalid.")
            used_indices += material_indices
            materials.append({
                "index": index, "name": name, "texture": diffuse,
                "color": color, "doubleSided": bool(flags & 1),
                "indexCount": material_indices, "sphereTexture": sphere,
                "sphereMode": sphere_mode, "toonTexture": toon,
            })
        if used_indices != index_count:
            raise ValueError("PMX material slots do not cover the mesh indices.")
        return {"source_type": "pmx", "source": source.name,
                "vertices": vertex_count, "materials": materials}, textures
    finally:
        reader.close()


def contained_path(root, relative):
    relative_path = Path(relative)
    if relative_path.is_absolute() or relative_path.drive:
        raise ValueError("Model texture paths must be relative to the model folder.")
    target = (root / relative_path).resolve()
    if not target.is_relative_to(root.resolve()):
        raise ValueError("Model texture path leaves the model folder.")
    return target


def addon_cache(archive_path):
    """Extract the already present add-on into an isolated workspace cache."""
    if not archive_path.is_file():
        raise FileNotFoundError("Local mmd_tools archive was not found: " + str(archive_path))
    digest = hashlib.sha256(archive_path.read_bytes()).hexdigest()[:16]
    parent = WORKSPACE / ".artifacts" / "model-import" / "addons"
    target = parent / digest
    marker = target / ".complete"
    if marker.is_file():
        return target
    temporary = parent / (digest + "." + str(os.getpid()) + ".staging")
    temporary.mkdir(parents=True, exist_ok=True)
    total = 0
    with zipfile.ZipFile(archive_path) as archive:
        for entry in archive.infolist():
            normalized = entry.filename.replace("\\", "/")
            if not normalized.startswith("mmd_tools/"):
                raise ValueError("Unexpected file in the local mmd_tools archive.")
            destination = contained_path(temporary, normalized)
            # An add-on is Python source; archive symlinks are unnecessary.
            if (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("Symbolic links are not accepted in the add-on archive.")
            total += entry.file_size
            if total > 64 * 1024 * 1024:
                raise ValueError("The local add-on exceeds the extraction limit.")
            if entry.is_dir():
                destination.mkdir(parents=True, exist_ok=True)
            else:
                destination.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(entry) as source, destination.open("wb") as output:
                    shutil.copyfileobj(source, output)
    if not (temporary / "mmd_tools" / "__init__.py").is_file():
        raise ValueError("The local archive does not contain mmd_tools.")
    (temporary / ".complete").write_text(digest, encoding="ascii")
    try:
        temporary.rename(target)
    except FileExistsError:
        if not marker.is_file():
            raise
    return target


def convert_textures(bpy, source, output_directory, texture_names):
    mapped = {}
    loaded = {}
    for index, name in enumerate(texture_names):
        path = contained_path(source.parent, name)
        if not path.is_file():
            raise FileNotFoundError("Missing PMX texture: " + name)
        if path.stat().st_size > MAX_FILE_BYTES:
            raise ValueError("PMX texture exceeds the import limit: " + name)
        image = bpy.data.images.load(str(path), check_existing=True)
        if image.size[0] <= 0 or image.size[1] <= 0 or image.size[0] * image.size[1] > 64_000_000:
            raise ValueError("PMX texture dimensions are unsupported: " + name)
        filename = "texture_{:03d}_{}.png".format(index, path.stem)
        destination = output_directory / filename
        if path.suffix.lower() == ".png":
            shutil.copyfile(path, destination)
        else:
            # Image.copy() does not copy Blender's decoded pixel buffer. Reload
            # the file into a separate image and force its pixels to load before
            # changing the output path. save() preserves image pixels without
            # applying the scene's render/view color transform.
            converted = bpy.data.images.load(str(path), check_existing=False)
            try:
                expected_pixels = converted.size[0] * converted.size[1] * 4
                if expected_pixels <= 0 or len(converted.pixels) != expected_pixels or not converted.has_data:
                    raise RuntimeError("Blender could not decode PMX texture pixels: " + name)
                converted.filepath_raw = str(destination)
                converted.file_format = "PNG"
                converted.save()
                if not destination.is_file() or destination.stat().st_size == 0:
                    raise RuntimeError("Blender did not save the converted PMX texture: " + name)
            finally:
                bpy.data.images.remove(converted)
        mapped[name] = filename
        loaded[str(path).casefold()] = destination
    # Update image references in the exported FBX to the output PNG copies.
    for image in bpy.data.images:
        if not image.filepath:
            continue
        try:
            original = str(Path(bpy.path.abspath(image.filepath)).resolve()).casefold()
            if original in loaded:
                image.filepath = str(loaded[original])
        except (OSError, ValueError):
            continue
    return mapped


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mmd-tools-zip", type=Path, required=True,
                        help="Path to your local mmd_tools add-on ZIP; it is not bundled.")
    args = parser.parse_args(argv)
    source, output = args.source.resolve(), args.output.resolve()
    if not source.is_file() or source.suffix.lower() != ".pmx":
        raise ValueError("--source must name an existing PMX file.")
    if output.suffix.lower() != ".fbx" or output.parent == source.parent:
        raise ValueError("--output must be an FBX in a separate staging folder.")
    metadata, texture_names = read_materials(source)
    for name in texture_names:
        if not contained_path(source.parent, name).is_file():
            raise FileNotFoundError("Missing PMX texture: " + name)
    import bpy
    if bpy.app.version < (3, 6, 0):
        raise RuntimeError("The converter requires Blender 3.6 or newer.")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    cache = addon_cache(args.mmd_tools_zip.resolve())
    # A normal Blender launch may have loaded an installed version before this
    # script. Replace it only in this background process, without saving prefs.
    existing = sys.modules.get("mmd_tools")
    if existing is not None:
        if hasattr(bpy.types.Object, "mmd_type"):
            existing.unregister()
        for module_name in list(sys.modules):
            if module_name == "mmd_tools" or module_name.startswith("mmd_tools."):
                del sys.modules[module_name]
    sys.path.insert(0, str(cache))
    importlib.invalidate_caches()
    addon = importlib.import_module("mmd_tools")
    addon.register()
    output.parent.mkdir(parents=True, exist_ok=True)
    # ImportPmx's UI operator catches errors and still returns FINISHED. Call the
    # same underlying importer directly so partial imports fail the queued job.
    importer = importlib.import_module("mmd_tools.core.pmx.importer")
    importer.PMXImporter().execute(
        filepath=str(source), types={"MESH", "ARMATURE", "MORPHS"},
        scale=0.08, clean_model=False, remove_doubles=False,
        rename_LR_bones=True, use_underscore=False,
    )
    objects = [obj for obj in bpy.context.scene.objects if obj.type in {"MESH", "ARMATURE"}
               and getattr(obj, "mmd_type", "NONE") not in {"RIGID_BODY", "JOINT"}]
    if not any(obj.type == "MESH" and len(obj.data.vertices) for obj in objects):
        raise RuntimeError("The PMX contains no renderable mesh.")
    if not any(obj.type == "ARMATURE" for obj in objects):
        raise RuntimeError("The PMX contains no armature.")
    converted = convert_textures(bpy, source, output.parent, texture_names)
    for material in metadata["materials"]:
        for key in ("texture", "sphereTexture", "toonTexture"):
            material[key] = converted.get(material[key], "")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.hide_set(False)
        obj.hide_viewport = False
        obj.select_set(True)
    bpy.context.view_layer.objects.active = next(obj for obj in objects if obj.type == "ARMATURE")
    result = bpy.ops.export_scene.fbx(
        filepath=str(output), check_existing=False, use_selection=True,
        object_types={"ARMATURE", "MESH"}, use_mesh_modifiers=False,
        add_leaf_bones=False, use_armature_deform_only=False, bake_anim=False,
        axis_forward="-Z", axis_up="Y", apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS", path_mode="RELATIVE",
        embed_textures=False,
    )
    if "FINISHED" not in result or not output.is_file() or output.stat().st_size == 0:
        raise RuntimeError("Blender did not produce the converted FBX.")
    metadata_path = output.parent / "PetMaterialMap.json"
    metadata_path.write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    print("DESKTOP_PET_PMX_CONVERTED " + json.dumps({
        "fbx": str(output), "metadata": str(metadata_path),
        "materials": len(metadata["materials"]), "textures": len(converted),
    }, ensure_ascii=False))


if __name__ == "__main__":
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    try:
        main(arguments)
    except Exception as error:
        print("DESKTOP_PET_PMX_CONVERSION_FAILED: " + str(error), file=sys.stderr)
        # --python-exit-code 1 lets the caller distinguish a failed background run.
        raise
