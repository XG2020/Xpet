#!/usr/bin/env python3
"""Export the Git-allowed source files without changing the workspace repository."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile


VERSION = "0.6.0"
MAX_FILE_BYTES = 50 * 1024 * 1024
GENERATED_DIRECTORIES = {
    ".git", ".artifacts", ".agents", ".codex", ".venv", "venv", "env",
    "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
    ".vs", ".idea", "node_modules", "library", "temp", "obj", "bin",
    "logs", "usersettings", "build", "builds", "dist", ".imports", ".legacy",
    "workspace-memory", "skill_books", "pet-motions",
}
MODEL_SUFFIXES = {
    ".fbx", ".pmx", ".pmd", ".blend", ".glb", ".gltf", ".obj", ".mtl",
    ".vrm", ".vmd", ".vpd", ".petmodel", ".petraw", ".abc", ".dae",
    ".3ds", ".max", ".mb", ".ma", ".usd", ".usda", ".usdc", ".usdz",
    ".unitypackage", ".assetbundle", ".bundle",
}
PRIVATE_KEY_SUFFIXES = {".pem", ".key", ".pfx", ".p12", ".jks", ".keystore"}
BINARY_SUFFIXES = {".exe", ".dll", ".pdb", ".pyc", ".pyo", ".dmp"}
LIBRARY_DIRECTORIES = {"pet-models", "pet-actions", "pet-skills"}
LIBRARY_NOTICES = {"readme.md", "license", "license.md", "license.txt", ".gitkeep", ".gitignore"}
UNITY_MANIFEST = "pet-frontend/pet-frontend/Packages/manifest.json"
UNITY_LOCK = "pet-frontend/pet-frontend/Packages/packages-lock.json"


class ExportError(Exception):
    """An export cannot safely continue."""


def linked_path(path: Path, root: Path) -> bool:
    """Reject symlinks and Windows junctions in a path below the source root."""
    current = path
    while current != root:
        if current.is_symlink() or getattr(current, "is_junction", lambda: False)():
            return True
        if current.parent == current:
            return True
        current = current.parent
    return False


def run_git(root: Path, git_directory: Path, *arguments: str) -> bytes:
    command = [
        "git", "-c", f"core.excludesFile={os.devnull}",
        f"--git-dir={git_directory}", f"--work-tree={root}", *arguments,
    ]
    result = subprocess.run(command, cwd=root, capture_output=True, check=False)
    if result.returncode:
        # Do not forward external output that could contain local configuration.
        raise ExportError(f"Git could not calculate the source file list (exit {result.returncode}).")
    return result.stdout


def source_files(root: Path) -> list[str]:
    artifacts = root / ".artifacts"
    if linked_path(artifacts, root):
        raise ExportError("Refusing linked directory: .artifacts")
    artifacts.mkdir(exist_ok=True)
    git_directory = Path(tempfile.mkdtemp(prefix="source-export-git-", dir=artifacts))
    # This index starts empty, so all allowed files are listed regardless of the
    # real repository's tracked/untracked state. Its .git is never touched.
    run_git(root, git_directory, "init", "--quiet")
    raw = run_git(root, git_directory, "ls-files", "--others", "--exclude-standard", "-z")
    return sorted(os.fsdecode(name) for name in raw.split(b"\0") if name)


def rejection_reason(relative: str) -> str | None:
    path = PurePosixPath(relative)
    parts = tuple(part.lower() for part in path.parts)
    if path.is_absolute() or not parts or ".." in parts or "\\" in relative:
        return "invalid path"
    if any(part in GENERATED_DIRECTORIES for part in parts[:-1]):
        return "generated or local data directory"
    if parts[0] == "verification" or parts[:2] == ("pet-frontend", "checks"):
        return "local verification output"
    if parts[0] in LIBRARY_DIRECTORIES and (len(parts) != 2 or parts[1] not in LIBRARY_NOTICES):
        return "user resource library"
    name = parts[-1]
    # Check both an external asset and Unity's metadata accompanying that asset.
    base_name = name[:-5] if name.endswith(".meta") else name
    suffix = PurePosixPath(base_name).suffix
    if base_name == ".env" or base_name.startswith(".env."):
        if not base_name.endswith((".example", ".sample")):
            return "local environment configuration"
    if base_name in {"runtime_settings.json", ".envrc", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519"}:
        return "local credentials or settings"
    if base_name.startswith(".settings-") and base_name.endswith(".tmp"):
        return "temporary settings"
    if suffix in PRIVATE_KEY_SUFFIXES:
        return "private key container"
    if re.search(r"\.(?:sqlite\d*|db)(?:[-.].*)?$", base_name):
        return "runtime database"
    if suffix == ".log" or ".log." in base_name:
        return "runtime log"
    if suffix in MODEL_SUFFIXES or re.search(r"\.blend\d+$", base_name):
        return "external model or animation asset"
    if suffix in BINARY_SUFFIXES or suffix in {".csproj", ".sln", ".slnx", ".rsp"}:
        return "generated binary or project file"
    return None


def checked_sources(root: Path, names: list[str]) -> list[tuple[str, Path, os.stat_result]]:
    files = []
    rejected = []
    for name in names:
        reason = rejection_reason(name)
        source = root.joinpath(*PurePosixPath(name).parts)
        if not reason and (linked_path(source, root) or not source.is_file()):
            reason = "linked or non-regular file"
        if reason:
            rejected.append((name, reason))
            continue
        stat = source.stat()
        if stat.st_size > MAX_FILE_BYTES:
            rejected.append((name, "larger than 50 MiB"))
        else:
            files.append((name, source, stat))
    if rejected:
        for name, reason in rejected:
            print(f"Rejected ({reason}): {name}", file=sys.stderr)
        raise ExportError("Update the project .gitignore before exporting these files.")
    if not files:
        raise ExportError("No source files were selected.")
    return files


def unity_export_manifest(root: Path) -> bytes | None:
    """Generate a standalone project's dependencies, without changing the input project.

    UnitySkills is an optional Editor automation tool. The runtime does not use
    it, but the model repacker requires its formerly transitive Newtonsoft package.
    A changed dependency graph must be resolved by Unity on the first import;
    do not ship an old lock file or claim to have performed that resolution here.
    """
    path = root / UNITY_MANIFEST
    if not path.is_file():
        return None
    manifest = json.loads(path.read_text(encoding="utf-8-sig"))
    dependencies = manifest.get("dependencies", {})
    if "com.besty.unity-skills" not in dependencies:
        return None
    del dependencies["com.besty.unity-skills"]
    dependencies.setdefault("com.unity.nuget.newtonsoft-json", "3.2.2")
    testables = [name for name in manifest.get("testables", [])
                 if name != "com.besty.unity-skills"]
    if testables:
        manifest["testables"] = testables
    else:
        manifest.pop("testables", None)
    for name, version in dependencies.items():
        if isinstance(version, str) and version.startswith("file:"):
            raise ExportError(f"Dependency needs a portable source before export: {name}")
    return (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def export(root: Path, destination_argument: str | None) -> None:
    if not shutil.which("git"):
        raise ExportError("Git is required. Install Git and ensure it is on PATH.")
    dist = root / "dist"
    requested = Path(destination_argument) if destination_argument else dist / f"Xpet-source-{VERSION}"
    if not requested.is_absolute():
        requested = root / requested
    if not requested.is_relative_to(root) or linked_path(requested, root):
        raise ExportError("The destination must be an unlinked directory inside this project's dist directory.")
    destination = requested.resolve()
    if destination == dist or not destination.is_relative_to(dist):
        raise ExportError("The destination must be a new directory inside this project's dist directory.")
    archive = destination.with_name(destination.name + ".zip")
    manifest = destination.with_name(destination.name + ".manifest.json")
    for output in (destination, archive, manifest):
        if output.exists() or output.is_symlink():
            raise ExportError(f"Output already exists: {output.relative_to(root).as_posix()}")

    files = checked_sources(root, source_files(root))
    generated_manifest = unity_export_manifest(root)
    if generated_manifest is not None:
        files = [entry for entry in files if entry[0] != UNITY_LOCK]
    destination.mkdir(parents=True, exist_ok=False)
    entries = []
    for name, source, initial_stat in files:
        target = destination.joinpath(*PurePosixPath(name).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        digest = hashlib.sha256()
        size = 0
        if name == UNITY_MANIFEST and generated_manifest is not None:
            with target.open("xb") as output_file:
                output_file.write(generated_manifest)
            digest.update(generated_manifest)
            size = len(generated_manifest)
        else:
            with source.open("rb") as input_file, target.open("xb") as output_file:
                for chunk in iter(lambda: input_file.read(1024 * 1024), b""):
                    size += len(chunk)
                    if size > MAX_FILE_BYTES:
                        raise ExportError(f"File grew beyond 50 MiB during export: {name}")
                    output_file.write(chunk)
                    digest.update(chunk)
        final_stat = source.stat()
        if final_stat.st_size != initial_stat.st_size or final_stat.st_mtime_ns != initial_stat.st_mtime_ns:
            raise ExportError(f"Source changed during export: {name}")
        entries.append({"path": name, "bytes": size, "sha256": digest.hexdigest()})

    total_bytes = sum(entry["bytes"] for entry in entries)
    with archive.open("xb") as archive_file:
        with zipfile.ZipFile(archive_file, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            for entry in entries:
                bundle.write(destination / entry["path"], f"{destination.name}/{entry['path']}")
    # Keep the inventory next to the archive; it is not itself a source file.
    with manifest.open("x", encoding="utf-8", newline="\n") as output_file:
        json.dump({"version": VERSION, "file_count": len(entries), "total_bytes": total_bytes,
                   "files": entries}, output_file, ensure_ascii=False, indent=2)
        output_file.write("\n")
    print(f"Exported {len(entries)} files ({total_bytes / (1024 * 1024):.2f} MiB).")
    for output in (destination, archive, manifest):
        print(output.relative_to(root).as_posix())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--destination", help=f"new directory inside dist (default: dist/Xpet-source-{VERSION})")
    arguments = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        export(root, arguments.destination)
    except (ExportError, OSError, ValueError) as exc:
        print(f"Export failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
