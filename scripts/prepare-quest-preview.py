#!/usr/bin/env python3
"""Prepare an isolated Unity 6000.3.25f1 Quest Link development project.

Copies already cached official packages; performs no download, account, license,
driver or system-runtime action. The desktop project remains unchanged.
"""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[1]
EDITOR_VERSION = "6000.3.25f1"
MARKER = "quest-preview-preparation.json"
DEFINE = "STROOP_XR_PREVIEW"
PACKAGES = {
    "com.unity.xr.openxr": "1.17.1",
    "com.unity.xr.management": "4.7.0",
    "com.unity.xr.core-utils": "2.5.2",
    "com.unity.inputsystem": "1.19.0",
    "com.unity.xr.legacyinputhelpers": "2.1.12",
}
SOURCE_FOLDERS = ("Assets", "Apps", "scripts")


def package_manifest(modules):
    # Embedded packages automatically enable their development tests. Local
    # file dependencies retain the original packages without enabling tests.
    return {"dependencies": dict(modules, **{name: "file:../LocalPackages/" + name for name in PACKAGES})}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def source_inventory(source):
    """All application source, including code outside Assets/Stroop."""
    inventory = {}
    for folder in SOURCE_FOLDERS:
        base = source / folder
        for path in sorted(base.rglob("*")):
            if path.is_file() and path.suffix.lower() in (".cs", ".shader", ".asmdef", ".asmref", ".py"):
                inventory[path.relative_to(source).as_posix()] = digest(path)
    return inventory


def verify_workspace_project(source, project):
    if project == source or project.is_relative_to(source) or not project.is_relative_to(source.parent):
        raise ValueError("The isolated project must be a separate directory inside the source workspace parent.")


def check_editor(editor):
    if not editor.is_file() or editor.name.lower() != "unity.exe":
        raise ValueError("An existing Windows Unity.exe is required; no Editor is installed by this script.")
    data = editor.parent / "Data"
    builtins = data / "Resources/PackageManager/BuiltInPackages"
    if not builtins.is_dir() or not (data / "PlaybackEngines/windowsstandalonesupport").is_dir():
        raise ValueError("The existing Editor must contain built-in packages and Windows Mono player support.")
    # Read the PE resource; do not execute Unity to discover its version.
    version = ctypes.WinDLL("version", use_last_error=True)
    version.GetFileVersionInfoSizeW.argtypes = (wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD))
    version.GetFileVersionInfoSizeW.restype = wintypes.DWORD
    version.GetFileVersionInfoW.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p)
    version.GetFileVersionInfoW.restype = wintypes.BOOL
    version.VerQueryValueW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.UINT))
    version.VerQueryValueW.restype = wintypes.BOOL
    unused = wintypes.DWORD()
    size = version.GetFileVersionInfoSizeW(str(editor), ctypes.byref(unused))
    if not size:
        raise ValueError("Could not read the installed Editor version resource.")
    data_buffer = ctypes.create_string_buffer(size)
    if not version.GetFileVersionInfoW(str(editor), 0, size, data_buffer):
        raise ValueError("Could not read the installed Editor version resource.")
    pointer, length = ctypes.c_void_p(), wintypes.UINT()
    if not version.VerQueryValueW(data_buffer, "\\VarFileInfo\\Translation", ctypes.byref(pointer), ctypes.byref(length)) or length.value < 4:
        raise ValueError("The Editor has no readable version translation.")
    translation = ctypes.cast(pointer, ctypes.POINTER(wintypes.WORD))
    key = "\\StringFileInfo\\%04x%04x\\ProductVersion" % (translation[0], translation[1])
    if not version.VerQueryValueW(data_buffer, key, ctypes.byref(pointer), ctypes.byref(length)):
        raise ValueError("The Editor has no readable ProductVersion.")
    actual = ctypes.wstring_at(pointer, length.value).rstrip("\0").split("_")[0]
    if actual != EDITOR_VERSION:
        raise ValueError("Expected Unity " + EDITOR_VERSION + "; found " + actual)
    return builtins


def find_packages(cache):
    found = {}
    # Unity uses both version names and content-derived names in PackageCache.
    for name, version in PACKAGES.items():
        candidates = []
        for directory in sorted(cache.glob(name + "@*")):
            manifest = directory / "package.json"
            if manifest.is_file():
                metadata = read_json(manifest)
                if metadata.get("name") == name and metadata.get("version") == version:
                    candidates.append((directory, metadata))
        if not candidates:
            raise ValueError("Missing cached official package: " + name + "@" + version)
        found[name] = candidates[0]
    return found


def package_closure(found, builtins, baseline):
    """Resolve only the pinned cache and the selected Editor's built-in modules."""
    modules = {}
    queue = list(baseline.get("dependencies", {}).items())
    for _, metadata in found.values():
        queue.extend(metadata.get("dependencies", {}).items())
    while queue:
        name, version = queue.pop()
        if name in PACKAGES:
            required = tuple(int(part) for part in version.split("."))
            selected = tuple(int(part) for part in PACKAGES[name].split("."))
            if selected < required:
                raise ValueError("Pinned package does not satisfy " + name + " " + version)
            continue
        if name in modules:
            if modules[name] != version:
                raise ValueError("Conflicting built-in module requirements: " + name)
            continue
        manifest = builtins / name / "package.json"
        if not name.startswith("com.unity.modules.") or not manifest.is_file():
            raise ValueError("Unresolved dependency; no network fallback is permitted: " + name + "@" + version)
        metadata = read_json(manifest)
        if metadata.get("version") != version:
            raise ValueError("Installed Editor built-in version mismatch: " + name)
        modules[name] = version
        queue.extend(metadata.get("dependencies", {}).items())
    return modules


def reject_link(path):
    if path.exists() or path.is_symlink():
        if path.is_symlink() or getattr(path.lstat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("Reparse points are not copied or overwritten: " + str(path))


def plain_tree(path):
    path = extended_path(path)
    reject_link(path)
    if path.is_dir():
        for child in path.rglob("*"):
            reject_link(child)


def extended_path(path):
    value = str(path)
    if sys.platform != "win32" or value.startswith("\\\\?\\"):
        return path
    return Path("\\\\?\\UNC\\" + value[2:] if value.startswith("\\\\") else "\\\\?\\" + value)


def copy_tree(source, destination):
    # Refuse source and destination links before any copy can follow them.
    plain_tree(source)
    for parent in (destination,) + tuple(destination.parents):
        reject_link(parent)
    plain_tree(destination)
    # Cached NativeTypes names can exceed MAX_PATH; use the Windows file API's
    # extended path spelling without changing the system long-path policy.
    shutil.copytree(extended_path(source), extended_path(destination), dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))


def verify_prepared(source, project):
    verify_workspace_project(source, project)
    # Unity also writes ProjectSettings, Library, Logs and Temp, so a matching
    # source inventory alone cannot prove isolation after a junction is added.
    for parent in (project,) + tuple(project.parents):
        reject_link(parent)
    plain_tree(project)
    record = read_json(project / MARKER)
    if record.get("format") != 1 or record.get("source") != str(source) or record.get("editor_version") != EDITOR_VERSION or record.get("package_layout") != "local-file":
        raise ValueError("This is not the matching isolated Quest preview project.")
    actual = source_inventory(source)
    copied = source_inventory(project)
    if actual != copied or actual != record.get("source_files"):
        raise ValueError("Preview source sets or bytes differ; refresh the isolated source snapshot first.")
    if digest(source / "Packages/manifest.json") != record.get("desktop_manifest_sha256"):
        raise ValueError("Desktop package baseline changed; prepare a new isolated project.")
    if read_json(project / "Packages/manifest.json") != package_manifest(record["builtin_modules"]):
        raise ValueError("Isolated package manifest differs from the approved package set.")
    if (project / "Assets/csc.rsp").read_text(encoding="utf-8").strip() != "-define:" + DEFINE:
        raise ValueError("The isolated preview compilation define is missing or changed.")
    for name, version in PACKAGES.items():
        package = project / "LocalPackages" / name / "package.json"
        metadata = read_json(package)
        if metadata.get("name") != name or metadata.get("version") != version or digest(package) != record["packages"][name]["manifest_sha256"]:
            raise ValueError("Local package metadata changed: " + name)
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", required=True, type=Path)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--package-cache", type=Path)
    parser.add_argument("--refresh-source", action="store_true",
                        help="Update only source files in an existing matching isolated project.")
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()
    try:
        if sys.platform != "win32":
            raise ValueError("Quest Link preview preparation requires Windows.")
        source, project, editor = ROOT.resolve(), args.project.resolve(), args.editor.resolve()
        verify_workspace_project(source, project)
        builtins = check_editor(editor)
        if args.refresh_source:
            record = read_json(project / MARKER)
            if record.get("format") != 1 or record.get("source") != str(source) or record.get("editor_version") != EDITOR_VERSION:
                raise ValueError("Refusing to refresh an unrelated directory.")
            if digest(source / "Packages/manifest.json") != record["desktop_manifest_sha256"]:
                raise ValueError("Package baseline changed; prepare a new isolated project.")
            current = source_inventory(source)
            if args.check_only:
                print("Source changes: " + str(sum(record["source_files"].get(p) != h for p, h in current.items())))
                return 0
            for parent in (project,) + tuple(project.parents):
                reject_link(parent)
            reject_link(project / MARKER)
            for folder in SOURCE_FOLDERS:
                plain_tree(source / folder)
                plain_tree(project / folder)
            for relative in record["source_files"]:
                if relative not in current:
                    old = (project / relative).resolve()
                    if not old.is_relative_to(project) or old.suffix.lower() not in (".cs", ".shader", ".asmdef", ".asmref", ".py"):
                        raise ValueError("Invalid previous source inventory path.")
                    if old.is_file():
                        old.unlink()
            for folder in SOURCE_FOLDERS:
                copy_tree(source / folder, project / folder)
            record["source_files"] = current
            write_json(project / MARKER, record)
            verify_prepared(source, project)
            print("Isolated source snapshot refreshed; project settings and packages retained.")
            return 0
        if project.exists():
            if args.check_only:
                verify_prepared(source, project)
                print("Existing isolated Quest preview source/package metadata verified.")
                return 0
            raise ValueError("Destination already exists; use --refresh-source for its source snapshot or choose a new directory.")
        if args.package_cache is None:
            raise ValueError("--package-cache is required when preparing a new project.")
        found = find_packages(args.package_cache.resolve())
        baseline = read_json(source / "Packages/manifest.json")
        modules = package_closure(found, builtins, baseline)
        if (source / "Assets/csc.rsp").exists():
            raise ValueError("The desktop source already has csc.rsp; review its compiler options before preparing.")
        metadata = {name: {"version": PACKAGES[name], "cache_path": str(path),
                          "manifest_sha256": digest(path / "package.json")}
                    for name, (path, _) in found.items()}
        if args.check_only:
            print(json.dumps({"packages": metadata, "builtin_modules": modules}, indent=2))
            return 0
        for parent in (project,) + tuple(project.parents):
            reject_link(parent)
        project.mkdir(parents=True, exist_ok=False)
        for folder in SOURCE_FOLDERS + ("ProjectSettings",):
            copy_tree(source / folder, project / folder)
        (project / "Packages").mkdir()
        for name, (path, _) in found.items():
            copy_tree(path, project / "LocalPackages" / name)
        write_json(project / "Packages/manifest.json", package_manifest(modules))
        (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: " + EDITOR_VERSION + "\n", encoding="utf-8")
        (project / "Assets/csc.rsp").write_text("-define:" + DEFINE + "\n", encoding="utf-8")
        write_json(project / MARKER, {"format": 1, "purpose": "Quest Link development preview; no apparatus acceptance",
                   "source": str(source), "editor": str(editor), "editor_version": EDITOR_VERSION,"package_layout": "local-file",
                   "desktop_manifest_sha256": digest(source / "Packages/manifest.json"),
                   "source_files": source_inventory(source), "packages": metadata, "builtin_modules": modules})
        verify_prepared(source, project)
        print("Prepared isolated project: " + str(project))
        print("Cached project dependencies copied. No Editor, player, headset, login or system configuration was launched.")
        return 0
    except (OSError, ValueError, KeyError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    sys.exit(main())
