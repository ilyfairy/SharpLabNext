#!/usr/bin/env python3
"""Deduplicate audited static payloads between Wine prefixes.

The operator-image mode links only immutable Framework/GAC trees. The shared
matrix parent additionally uses a versioned, positive policy for static
Wine/Windows runtime payloads. Registry, user, cache, setup, native-image and
other state paths are never eligible. A link is created only after metadata
and SHA-256 content match, and ``--freeze`` removes write bits from the linked
pair. The generated manifest is deterministic and can be checked later.
"""

from __future__ import annotations

import argparse
import errno
import hashlib
import json
import os
import stat
import sys
import tempfile
from functools import lru_cache
from pathlib import Path, PurePosixPath


SCHEMA_VERSION = 1
STRATEGY = "hardlink-immutable-v1"
MATRIX_STRATEGY = "hardlink-immutable-matrix-v1"
STATIC_RUNTIME_MATRIX_STRATEGY = "hardlink-static-runtime-matrix-v1"
STATIC_RUNTIME_POLICY_ID = "wine-static-runtime-payload-v1"
MAX_FILE_BYTES = 512 * 1024 * 1024
IMMUTABLE_ROOTS = (
    PurePosixPath("drive_c/windows/Microsoft.NET"),
    PurePosixPath("drive_c/windows/assembly"),
)
MUTABLE_COMPONENTS = frozenset(
    {
        "cache",
        "caches",
        "download",
        "downloads",
        "log",
        "logs",
        "nativeimages",
        "ngen",
        "setupcache",
        "temp",
        "tmp",
    }
)
MUTABLE_COMPONENT_PREFIXES = (
    "nativeimages_",
    "ngen_",
    "temporary asp.net files",
)
MUTABLE_SUFFIXES = (".bak", ".etl", ".lock", ".log", ".tmp")

STATIC_RUNTIME_PROTECTED_COMPONENTS = frozenset(
    {
        *MUTABLE_COMPONENTS,
        "catroot",
        "catroot2",
        "config",
        "repository",
        "spool",
        "tasks",
        "winevt",
    }
)
STATIC_RUNTIME_PROTECTED_NAMES = frozenset(
    {"pending.xml", "pending.xml.bad", "poqexec.log"}
)
STATIC_RUNTIME_DIRECT_EXTENSIONS = frozenset(
    {
        ".acm",
        ".ax",
        ".com",
        ".cpl",
        ".dll",
        ".drv",
        ".exe",
        ".mui",
        ".nls",
        ".ocx",
        ".sys",
        ".tlb",
        ".vxd",
    }
)
STATIC_RUNTIME_RESOURCE_EXTENSIONS = frozenset({".dll", ".msstyles", ".mui"})
XATTR_POLICY = {
    "mode": "allowlisted-content-identity-v1",
    "allowedNames": ["user.DOSATTRIB"],
    "valueEncoding": "ascii-hex-mask",
    "maxValueBytes": 32,
    "outputNormalization": "strip-allowlisted-before-identity-v1",
}
STATIC_RUNTIME_POLICY_DOCUMENT = {
    "schemaVersion": 1,
    "id": STATIC_RUNTIME_POLICY_ID,
    "algorithm": "structured-case-insensitive-posix-rules-v2",
    "nodeConstraints": {
        "type": "regular-file",
        "maxBytes": MAX_FILE_BYTES,
        "xattrs": XATTR_POLICY,
        "identity": ["size", "mode", "uid", "gid", "sha256", "xattrs"],
        "sameFilesystem": True,
        "freezeMode": "preserve-rwx-remove-write-and-special-v1",
    },
    "protected": {
        "components": sorted(STATIC_RUNTIME_PROTECTED_COMPONENTS),
        "componentPrefixes": sorted(MUTABLE_COMPONENT_PREFIXES),
        "componentSuffixes": ["cache"],
        "temporaryComponentPrefix": "temporary ",
        "names": sorted(STATIC_RUNTIME_PROTECTED_NAMES),
        "suffixes": sorted((*MUTABLE_SUFFIXES, ".reg")),
        "roots": ["dosdevices", "drive_c/users"],
    },
    "eligible": {
        "subtreeRoots": [
            *[root.as_posix() for root in IMMUTABLE_ROOTS],
            "drive_c/windows/winsxs",
            "drive_c/program files/reference assemblies",
            "drive_c/program files (x86)/reference assemblies",
        ],
        "directExtensionRules": [
            {
                "roots": [
                    "drive_c/windows/system32",
                    "drive_c/windows/syswow64",
                ],
                "extensions": sorted(STATIC_RUNTIME_DIRECT_EXTENSIONS),
            },
            {
                "roots": [
                    "drive_c/windows/system32/drivers",
                    "drive_c/windows/syswow64/drivers",
                ],
                "extensions": [".sys"],
            },
        ],
        "subtreeExtensionRules": [
            {
                "roots": ["drive_c/windows/resources"],
                "extensions": sorted(STATIC_RUNTIME_RESOURCE_EXTENSIONS),
            },
        ],
    },
}
STATIC_RUNTIME_POLICY_SHA256 = "sha256:" + hashlib.sha256(
    json.dumps(
        STATIC_RUNTIME_POLICY_DOCUMENT,
        ensure_ascii=True,
        sort_keys=True,
        separators=(",", ":"),
    ).encode()
).hexdigest()
STATIC_RUNTIME_MATCHER_PROTECTED_COMPONENTS = frozenset(
    STATIC_RUNTIME_POLICY_DOCUMENT["protected"]["components"]
)
STATIC_RUNTIME_MATCHER_PROTECTED_SUFFIXES = tuple(
    STATIC_RUNTIME_POLICY_DOCUMENT["protected"]["suffixes"]
)


class LayoutError(RuntimeError):
    pass


def fail(message: str) -> "NoReturn":
    raise LayoutError(message)


def canonical_root(value: str, label: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        fail(f"{label} must be an absolute path")
    # Resolve only for validation.  A symlinked prefix would make the layout
    # boundary ambiguous, so reject it rather than silently following it.
    resolved = path.resolve(strict=False)
    if resolved != path:
        fail(f"{label} must not contain symlinked path components")
    if path == Path(path.anchor):
        fail(f"{label} must not be the filesystem root")
    if not path.is_dir():
        fail(f"{label} does not exist as a directory")
    if not (path / "drive_c").is_dir():
        fail(f"{label} is not a Wine prefix (drive_c is missing)")
    return path


def manifest_path(value: str, create_parent: bool) -> Path:
    path = Path(value)
    if not path.is_absolute():
        fail("manifest must be an absolute path")
    if path.parent.resolve(strict=False) != path.parent:
        fail("manifest path must not contain symlinked components")
    if path.exists() and path.is_symlink():
        fail("manifest must not be a symlink")
    if create_parent:
        path.parent.mkdir(parents=True, exist_ok=True)
    elif not path.parent.is_dir():
        fail("layout manifest parent directory does not exist")
    return path


def relative_immutable(path: Path, root: Path) -> PurePosixPath | None:
    try:
        relative = PurePosixPath(path.relative_to(root).as_posix())
    except ValueError:
        return None
    # The allow-list is intentionally narrow.  In particular, system.reg,
    # user.reg, dosdevices, and all user/cache trees are outside it.
    for allowed in IMMUTABLE_ROOTS:
        try:
            relative.relative_to(allowed)
            break
        except ValueError:
            continue
    else:
        return None
    components = {component.lower() for component in relative.parts}
    if components & MUTABLE_COMPONENTS or any(
        component.startswith(prefix)
        for component in components
        for prefix in MUTABLE_COMPONENT_PREFIXES
    ):
        return None
    if relative.name.lower().endswith(MUTABLE_SUFFIXES):
        return None
    return relative


def static_runtime_policy_identity() -> dict[str, str]:
    return {
        "id": STATIC_RUNTIME_POLICY_ID,
        "sha256": STATIC_RUNTIME_POLICY_SHA256,
    }


@lru_cache(maxsize=None)
def policy_path(value: str) -> tuple[str, ...]:
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        fail("static runtime policy contains a non-canonical path")
    return tuple(part.lower() for part in path.parts)


def path_is_under(path: tuple[str, ...], root: tuple[str, ...]) -> bool:
    return path[: len(root)] == root


def relative_static_runtime(path: Path, root: Path) -> PurePosixPath | None:
    """Return an eligible path under the static runtime payload policy."""
    try:
        relative = PurePosixPath(path.relative_to(root).as_posix())
    except ValueError:
        return None
    lowered = tuple(part.lower() for part in relative.parts)
    if not lowered:
        return None
    policy = STATIC_RUNTIME_POLICY_DOCUMENT
    protected = policy["protected"]
    eligible = policy["eligible"]
    name = lowered[-1]
    components = set(lowered)
    if (
        any(path_is_under(lowered, policy_path(value)) for value in protected["roots"])
        or name in protected["names"]
        or name.endswith(STATIC_RUNTIME_MATCHER_PROTECTED_SUFFIXES)
        or components & STATIC_RUNTIME_MATCHER_PROTECTED_COMPONENTS
        or any(
            component.startswith(prefix)
            for component in lowered
            for prefix in protected["componentPrefixes"]
        )
        or any(
            component.endswith(suffix)
            for component in lowered
            for suffix in protected["componentSuffixes"]
        )
        or any(
            component.startswith(protected["temporaryComponentPrefix"])
            for component in lowered
        )
    ):
        return None

    suffix = PurePosixPath(name).suffix
    if any(path_is_under(lowered, policy_path(value)) for value in eligible["subtreeRoots"]):
        return relative
    for rule in eligible["directExtensionRules"]:
        if suffix not in rule["extensions"]:
            continue
        if any(lowered[:-1] == policy_path(value) for value in rule["roots"]):
            return relative
    for rule in eligible["subtreeExtensionRules"]:
        if suffix not in rule["extensions"]:
            continue
        if any(path_is_under(lowered, policy_path(value)) for value in rule["roots"]):
            return relative
    return None


def read_supported_xattrs(path: Path) -> tuple[tuple[str, bytes], ...]:
    listxattr = getattr(os, "listxattr", None)
    if listxattr is None:
        return ()
    try:
        attributes = listxattr(path, follow_symlinks=False)
    except TypeError:
        # A platform exposing listxattr without no-follow support cannot safely
        # inspect a link. Regular files are still inspectable.
        if path.is_symlink():
            fail(f"could not inspect symlink extended attributes: {path}")
        attributes = listxattr(path)
    except OSError as exception:
        unsupported = {errno.ENOSYS, errno.ENOTSUP}
        if hasattr(errno, "EOPNOTSUPP"):
            unsupported.add(errno.EOPNOTSUPP)
        if exception.errno in unsupported:
            return ()
        fail(f"could not inspect extended attributes for {path}: {exception}")
    allowed = set(XATTR_POLICY["allowedNames"])
    result: list[tuple[str, bytes]] = []
    for attribute in attributes:
        name = attribute.decode("utf-8", "strict") if isinstance(attribute, bytes) else attribute
        if name not in allowed:
            fail(f"static runtime payload has unsupported extended attributes: {path}")
        try:
            value = os.getxattr(path, attribute, follow_symlinks=False)
        except OSError as exception:
            fail(f"could not read extended attribute {name} for {path}: {exception}")
        validate_xattr_value(name, value, f"static runtime payload extended attribute on {path}")
        result.append((name, bytes(value)))
    return tuple(sorted(result, key=lambda item: (item[0], item[1])))


def validate_xattr_value(name: str, value: bytes, label: str) -> None:
    """Validate the allowlisted xattr contract used by bytes and manifests."""
    if name not in set(XATTR_POLICY["allowedNames"]):
        fail(f"{label} has unsupported name")
    if len(value) > int(XATTR_POLICY["maxValueBytes"]):
        fail(f"{label} is too large")
    if name == "user.DOSATTRIB":
        try:
            text = value.decode("ascii")
        except UnicodeDecodeError:
            fail(f"{label} has invalid encoding")
        if not text.startswith("0x") or len(text) < 3 or any(
            character not in "0123456789abcdefABCDEF" for character in text[2:]
        ):
            fail(f"{label} has invalid value")


def normalize_supported_xattrs(root: Path) -> int:
    """Strip validated DOS metadata that OCI export cannot preserve."""
    if root.is_symlink() or not root.is_dir():
        fail(f"xattr normalization root must be a real directory: {root}")
    nodes = [root]
    for current, directories, files in os.walk(root, followlinks=False):
        directories.sort()
        files.sort()
        current_path = Path(current)
        nodes.extend(current_path / name for name in [*directories, *files])

    removed = 0
    remove_xattr = getattr(os, "removexattr", None)
    for path in nodes:
        attributes = read_supported_xattrs(path)
        if attributes and remove_xattr is None:
            fail(f"could not normalize extended attributes for {path}")
        for name, _value in attributes:
            try:
                remove_xattr(path, name, follow_symlinks=False)
            except TypeError:
                if path.is_symlink():
                    fail(f"could not safely normalize symlink extended attributes: {path}")
                remove_xattr(path, name)
            except OSError as exception:
                fail(f"could not normalize extended attribute {name} for {path}: {exception}")
            removed += 1
        if read_supported_xattrs(path):
            fail(f"extended attributes remained after normalization: {path}")
    return removed


def manifest_xattrs(value: object, label: str) -> tuple[tuple[str, bytes], ...]:
    if not isinstance(value, list):
        fail(f"{label} xattrs are missing")
    result: list[tuple[str, bytes]] = []
    names: set[str] = set()
    for item in value:
        if not isinstance(item, dict) or not isinstance(item.get("name"), str) or not isinstance(item.get("value"), str):
            fail(f"{label} xattrs are invalid")
        name = item["name"]
        if name in names:
            fail(f"{label} xattrs contain a duplicate name")
        try:
            data = bytes.fromhex(item["value"])
        except (TypeError, ValueError):
            fail(f"{label} xattrs are invalid")
        validate_xattr_value(name, data, f"{label} xattr {name}")
        names.add(name)
        result.append((name, data))
    return tuple(sorted(result, key=lambda entry: (entry[0], entry[1])))


def encoded_xattrs(value: tuple[tuple[str, bytes], ...]) -> list[dict[str, str]]:
    return [{"name": name, "value": data.hex()} for name, data in value]


def enumerate_policy_files(root: Path, predicate) -> dict[str, Path]:
    result: dict[str, Path] = {}
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        directories[:] = [
            name for name in directories if not (current_path / name).is_symlink()
        ]
        for name in files:
            candidate = current_path / name
            relative = predicate(candidate, root)
            if relative is None or candidate.is_symlink():
                continue
            try:
                info = candidate.stat()
            except OSError as exception:
                fail(f"could not stat policy file {candidate}: {exception}")
            if not stat.S_ISREG(info.st_mode):
                continue
            read_supported_xattrs(candidate)
            if info.st_size > int(STATIC_RUNTIME_POLICY_DOCUMENT["nodeConstraints"]["maxBytes"]):
                fail(f"policy file is unexpectedly large: {candidate}")
            key = relative.as_posix()
            if key in result:
                fail(f"duplicate policy path discovered: {key}")
            result[key] = candidate
    return result


def enumerate_files(root: Path) -> dict[str, Path]:
    result: dict[str, Path] = {}
    for allowed in IMMUTABLE_ROOTS:
        directory = root / Path(*allowed.parts)
        if not directory.is_dir():
            continue
        for current, directories, files in os.walk(directory, followlinks=False):
            current_path = Path(current)
            # Refuse symlinked directories instead of walking outside the
            # prefix.  Empty/missing optional trees are fine.
            kept_directories: list[str] = []
            for name in directories:
                candidate = current_path / name
                if candidate.is_symlink():
                    continue
                kept_directories.append(name)
            directories[:] = kept_directories
            for name in files:
                candidate = current_path / name
                relative = relative_immutable(candidate, root)
                if relative is None or candidate.is_symlink():
                    continue
                try:
                    info = candidate.stat()
                except OSError as exception:
                    fail(f"could not stat immutable file {candidate}: {exception}")
                if not stat.S_ISREG(info.st_mode):
                    continue
                if info.st_size > MAX_FILE_BYTES:
                    fail(f"immutable file is unexpectedly large: {candidate}")
                key = relative.as_posix()
                if key in result:
                    fail(f"duplicate immutable path discovered: {key}")
                result[key] = candidate
    return result


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            while True:
                block = stream.read(1024 * 1024)
                if not block:
                    break
                hasher.update(block)
    except OSError as exception:
        fail(f"could not hash immutable file {path}: {exception}")
    return hasher.hexdigest()


def file_key(path: Path) -> tuple[int, int, int, int, str, tuple[tuple[str, bytes], ...]]:
    try:
        info = path.stat()
    except OSError as exception:
        fail(f"could not stat immutable file {path}: {exception}")
    return (
        info.st_size,
        info.st_mode & 0o7777,
        info.st_uid,
        info.st_gid,
        digest(path),
        read_supported_xattrs(path),
    )


def immutable_mode(mode: int) -> int:
    # Keep execute/read bits but remove all writes.  Do not preserve setuid or
    # setgid bits when making a file shared across independently selected
    # prefixes.
    return mode & 0o777 & ~0o222


def make_link(source: Path, target: Path, mode: int) -> None:
    try:
        source_info = source.stat()
        target_info = target.stat()
        if source_info.st_dev != target_info.st_dev:
            fail(f"prefixes are on different filesystems: {source} and {target}")
        if os.path.samefile(source, target):
            return
        temporary = target.with_name(
            f".{target.name}.sharplabnext-link-{os.getpid()}"
        )
        if temporary.exists() or temporary.is_symlink():
            fail(f"temporary link path already exists: {temporary}")
        os.link(source, temporary, follow_symlinks=False)
        os.chmod(temporary, mode, follow_symlinks=False)
        os.replace(temporary, target)
    except LayoutError:
        raise
    except OSError as exception:
        try:
            if "temporary" in locals() and (temporary.exists() or temporary.is_symlink()):
                temporary.unlink()
        except OSError:
            pass
        fail(f"could not create immutable hard link {source} -> {target}: {exception}")


def build_manifest(source: Path, target: Path, manifest: Path, freeze: bool) -> dict:
    source_files = enumerate_files(source)
    target_files = enumerate_files(target)
    candidates: dict[
        tuple[int, int, int, int, str, tuple[tuple[str, bytes], ...]], Path
    ] = {}
    for relative, path in sorted(source_files.items()):
        candidates.setdefault(file_key(path), path)

    links: list[dict[str, object]] = []
    for relative, target_path in sorted(target_files.items()):
        key = file_key(target_path)
        source_path = candidates.get(key)
        if source_path is None:
            continue
        source_info = source_path.stat()
        target_info = target_path.stat()
        if source_info.st_uid != target_info.st_uid or source_info.st_gid != target_info.st_gid:
            continue
        mode = source_info.st_mode & 0o7777
        if freeze:
            mode = immutable_mode(mode)
        elif mode & 0o222:
            # Without an explicit freeze request, never create a link that
            # could be modified through one prefix and affect the other.
            continue
        make_link(source_path, target_path, mode)
        links.append(
            {
                "source": PurePosixPath(source_path.relative_to(source).as_posix()).as_posix(),
                "target": relative,
                "size": target_info.st_size,
                "sha256": key[4],
                "mode": format(mode, "04o"),
                "xattrs": encoded_xattrs(key[5]),
            }
        )

    links.sort(key=lambda item: (str(item["target"]), str(item["source"])))
    return {
        "schemaVersion": SCHEMA_VERSION,
        "strategy": STRATEGY,
        "sourcePrefix": source.name,
        "targetPrefix": target.name,
        "allowListedRoots": [root.as_posix() for root in IMMUTABLE_ROOTS],
        "freeze": freeze,
        "linkedFileCount": len(links),
        "linkedBytes": sum(int(item["size"]) for item in links),
        "links": links,
    }


def write_manifest(path: Path, value: dict) -> None:
    payload = (json.dumps(value, indent=2, ensure_ascii=True, sort_keys=False) + "\n").encode()
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False
        ) as stream:
            temporary = Path(stream.name)
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except OSError as exception:
        try:
            if "temporary" in locals() and temporary.exists():
                temporary.unlink()
        except OSError:
            pass
        fail(f"could not write layout manifest: {exception}")


def read_manifest_document(path: Path, expected_strategy: str) -> dict:
    try:
        if path.parent.resolve(strict=False) != path.parent:
            fail("layout manifest path must not contain symlinked components")
        if path.is_symlink():
            fail("layout manifest must not be a symlink")
        value = json.loads(path.read_text(encoding="utf-8"))
    except LayoutError:
        raise
    except (OSError, json.JSONDecodeError) as exception:
        fail(f"could not read layout manifest: {exception}")
    if not isinstance(value, dict):
        fail("layout manifest must be a JSON object")
    if value.get("schemaVersion") != SCHEMA_VERSION or value.get("strategy") != expected_strategy:
        fail("layout manifest schema or strategy is unsupported")
    links = value.get("links")
    if not isinstance(links, list):
        fail("layout manifest links must be an array")
    return value


def read_manifest(path: Path, expected_strategy: str = STRATEGY) -> dict:
    value = read_manifest_document(path, expected_strategy)
    if value.get("allowListedRoots") != [root.as_posix() for root in IMMUTABLE_ROOTS]:
        fail("layout manifest allow-list does not match this helper")
    return value


def safe_manifest_relative(value: object, label: str) -> PurePosixPath:
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        fail(f"layout manifest {label} path is invalid")
    relative = PurePosixPath(value)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in relative.parts):
        fail(f"layout manifest {label} path is not canonical")
    return relative


def checked_manifest_node(root: Path, relative: PurePosixPath, label: str) -> Path:
    current = root
    for index, part in enumerate(relative.parts):
        current /= part
        try:
            info = current.lstat()
        except OSError as exception:
            fail(f"could not inspect layout manifest {label} path {current}: {exception}")
        if stat.S_ISLNK(info.st_mode):
            fail(f"layout manifest {label} path has a symlinked component: {current}")
        if index < len(relative.parts) - 1 and not stat.S_ISDIR(info.st_mode):
            fail(f"layout manifest {label} path has a non-directory component: {current}")
    return current


def verify_manifest(source: Path, target: Path, manifest_path_value: Path) -> dict:
    value = read_manifest(manifest_path_value)
    if value.get("sourcePrefix") != source.name or value.get("targetPrefix") != target.name:
        fail("layout manifest prefix identities do not match the selected prefixes")
    if value.get("freeze") is not True:
        fail("layout manifest must declare frozen immutable links")
    links = value["links"]
    seen_targets: set[str] = set()
    # One canonical source file may safely back several identical target
    # files (for example Framework and GAC copies), so only target paths must
    # be unique.
    for entry in links:
        if not isinstance(entry, dict):
            fail("layout manifest contains a non-object link entry")
        source_relative = safe_manifest_relative(entry.get("source"), "source")
        target_relative = safe_manifest_relative(entry.get("target"), "target")
        target_relative_text = target_relative.as_posix()
        if target_relative_text in seen_targets:
            fail("layout manifest contains duplicate link paths")
        seen_targets.add(target_relative_text)
        source_path = checked_manifest_node(source, source_relative, "source")
        target_path = checked_manifest_node(target, target_relative, "target")
        if relative_immutable(source_path, source) is None or relative_immutable(target_path, target) is None:
            fail("layout manifest link escapes the immutable allow-list")
        if source_path.is_symlink() or target_path.is_symlink():
            fail("layout manifest link points to a symlink")
        try:
            source_info = source_path.stat()
            target_info = target_path.stat()
            if not stat.S_ISREG(source_info.st_mode) or not stat.S_ISREG(target_info.st_mode):
                fail("layout manifest link is not a regular file")
            if not os.path.samefile(source_path, target_path):
                fail("layout manifest link is no longer a hard link")
            if source_info.st_size != int(entry.get("size")):
                fail("layout manifest link size changed")
            if digest(source_path) != entry.get("sha256"):
                fail("layout manifest link content digest changed")
            expected_mode = int(str(entry.get("mode")), 8)
            if source_info.st_mode & 0o7777 != expected_mode or target_info.st_mode & 0o7777 != expected_mode:
                fail("layout manifest link mode changed")
            if expected_mode & 0o222:
                fail("layout manifest link is writable")
        except (OSError, TypeError, ValueError) as exception:
            if isinstance(exception, LayoutError):
                raise
            fail(f"could not verify layout manifest link: {exception}")
    if int(value.get("linkedFileCount", -1)) != len(links):
        fail("layout manifest linked file count is inconsistent")
    if int(value.get("linkedBytes", -1)) != sum(int(entry["size"]) for entry in links):
        fail("layout manifest linked byte count is inconsistent")
    return value


def parse_matrix_prefix(value: str) -> tuple[str, Path]:
    """Parse the stable ``id=/absolute/prefix`` matrix input."""
    if "=" not in value:
        fail("matrix prefix must use ID=/absolute/prefix")
    identifier, path_value = value.split("=", 1)
    if not identifier or not all(
        character.islower() or character.isdigit() or character in "._-"
        for character in identifier
    ) or identifier[0] not in "abcdefghijklmnopqrstuvwxyz0123456789":
        fail("matrix prefix ID is not a safe lowercase identifier")
    return identifier, canonical_root(path_value, f"matrix prefix {identifier}")


def matrix_manifest_path(value: str, create_parent: bool) -> Path:
    return manifest_path(value, create_parent=create_parent)


class MatrixManifestBuilder:
    """Incrementally deduplicate a deterministic sequence of matrix prefixes.

    The parent assembler copies one mounted prefix at a time and immediately
    calls ``add_prefix``. This bounds transient writable storage to the
    deduplicated output plus one not-yet-deduplicated prefix instead of all raw
    rows at once.
    """

    def __init__(
        self,
        freeze: bool,
        *,
        enumerate_function=enumerate_files,
        strategy: str = MATRIX_STRATEGY,
        policy: dict[str, str] | None = None,
    ):
        self.freeze = freeze
        self.enumerate_function = enumerate_function
        self.strategy = strategy
        self.policy = policy
        self.prefixes: dict[str, Path] = {}
        self.candidates: dict[
            tuple[int, int, int, int, str, tuple[tuple[str, bytes], ...]], tuple[str, Path]
        ] = {}
        self.links: list[dict[str, object]] = []

    def add_prefix(self, identifier: str, root: Path) -> None:
        if identifier in self.prefixes:
            fail("matrix prefix IDs must be unique")
        if self.prefixes and identifier <= next(reversed(self.prefixes)):
            fail("matrix prefixes must be added in ordinal identifier order")
        self.prefixes[identifier] = root

        for relative, target_path in sorted(self.enumerate_function(root).items()):
            key = file_key(target_path)
            source = self.candidates.get(key)
            if source is None:
                self.candidates[key] = (identifier, target_path)
                continue
            if source[0] == identifier:
                continue
            source_identifier, source_path = source
            source_info = source_path.stat()
            target_info = target_path.stat()
            if source_info.st_uid != target_info.st_uid or source_info.st_gid != target_info.st_gid:
                continue
            mode = source_info.st_mode & 0o7777
            if self.freeze:
                mode = immutable_mode(mode)
            elif mode & 0o222:
                continue
            make_link(source_path, target_path, mode)
            self.links.append(
                {
                    "sourcePrefix": source_identifier,
                    "source": PurePosixPath(
                        source_path.relative_to(self.prefixes[source_identifier]).as_posix()
                    ).as_posix(),
                    "targetPrefix": identifier,
                    "target": relative,
                    "size": target_info.st_size,
                    "sha256": key[4],
                    "mode": format(mode, "04o"),
                    "xattrs": encoded_xattrs(key[5]),
                }
            )

    def manifest(self) -> dict:
        if len(self.prefixes) < 2:
            fail("matrix mode requires at least two prefixes")
        links = sorted(
            self.links,
            key=lambda item: (
                str(item["targetPrefix"]),
                str(item["target"]),
                str(item["sourcePrefix"]),
            ),
        )
        value = {
            "schemaVersion": SCHEMA_VERSION,
            "strategy": self.strategy,
            "prefixes": sorted(self.prefixes),
            "freeze": self.freeze,
            "linkedFileCount": len(links),
            "linkedBytes": sum(int(item["size"]) for item in links),
            "links": links,
        }
        if self.policy is None:
            value["allowListedRoots"] = [root.as_posix() for root in IMMUTABLE_ROOTS]
        else:
            value["policy"] = self.policy
        return value


class StaticRuntimeMatrixManifestBuilder(MatrixManifestBuilder):
    def __init__(self, freeze: bool = True):
        super().__init__(
            freeze,
            enumerate_function=lambda root: enumerate_policy_files(
                root,
                relative_static_runtime,
            ),
            strategy=STATIC_RUNTIME_MATRIX_STRATEGY,
            policy=static_runtime_policy_identity(),
        )


def build_matrix_manifest(
    prefixes: list[tuple[str, Path]], manifest: Path, freeze: bool
) -> dict:
    if len(prefixes) < 2:
        fail("matrix mode requires at least two prefixes")
    if len({identifier for identifier, _ in prefixes}) != len(prefixes):
        fail("matrix prefix IDs must be unique")
    builder = MatrixManifestBuilder(freeze)
    for identifier, root in sorted(prefixes, key=lambda item: item[0]):
        builder.add_prefix(identifier, root)
    return builder.manifest()


def read_matrix_manifest(path: Path) -> dict:
    value = read_manifest(path, expected_strategy=MATRIX_STRATEGY)
    prefixes = value.get("prefixes")
    if not isinstance(prefixes, list) or not prefixes or any(
        not isinstance(identifier, str) for identifier in prefixes
    ):
        fail("matrix layout manifest prefixes are invalid")
    return value


def read_static_runtime_matrix_manifest(path: Path) -> dict:
    value = read_manifest_document(path, STATIC_RUNTIME_MATRIX_STRATEGY)
    if value.get("policy") != static_runtime_policy_identity():
        fail("matrix layout manifest static runtime policy does not match this helper")
    prefixes = value.get("prefixes")
    if not isinstance(prefixes, list) or not prefixes or any(
        not isinstance(identifier, str) for identifier in prefixes
    ):
        fail("matrix layout manifest prefixes are invalid")
    return value


def audit_cross_prefix_hardlinks(
    prefix_map: dict[str, Path],
    declared_paths: set[tuple[str, str]],
    predicate,
) -> None:
    inode_groups: dict[tuple[int, int], list[tuple[str, str, Path]]] = {}
    for identifier, root in sorted(prefix_map.items()):
        for current, directories, files in os.walk(root, followlinks=False):
            current_path = Path(current)
            directories.sort()
            files.sort()
            for name in [*directories, *files]:
                candidate = current_path / name
                try:
                    info = candidate.lstat()
                except OSError as exception:
                    fail(f"could not inspect matrix prefix node {candidate}: {exception}")
                if stat.S_ISDIR(info.st_mode):
                    continue
                if not stat.S_ISREG(info.st_mode) and not stat.S_ISLNK(info.st_mode):
                    fail(f"matrix prefix contains an unsupported special node: {candidate}")
                relative = PurePosixPath(candidate.relative_to(root).as_posix()).as_posix()
                inode_groups.setdefault((info.st_dev, info.st_ino), []).append(
                    (identifier, relative, candidate)
                )

    for locations in inode_groups.values():
        if len({identifier for identifier, _relative, _path in locations}) < 2:
            continue
        actual_paths = {(identifier, relative) for identifier, relative, _path in locations}
        if not actual_paths.issubset(declared_paths):
            unexpected = sorted(actual_paths - declared_paths)
            fail(f"matrix contains an undeclared cross-prefix hard link: {unexpected[0]}")
        for identifier, relative, path in locations:
            if predicate(path, prefix_map[identifier]) is None:
                fail(f"matrix contains a cross-prefix hard link outside policy: {identifier}:{relative}")


def verify_matrix_manifest_with_policy(
    prefixes: list[tuple[str, Path]],
    value: dict,
    predicate,
    node_constraints: dict[str, object] | None = None,
) -> dict:
    prefix_map = dict(prefixes)
    expected_ids = sorted(prefix_map)
    if value.get("prefixes") != expected_ids:
        fail("matrix layout manifest prefix identities do not match selected prefixes")
    if value.get("freeze") is not True:
        fail("matrix layout manifest must declare frozen immutable links")

    links = value["links"]
    seen_targets: set[tuple[str, str]] = set()
    declared_paths: set[tuple[str, str]] = set()
    for entry in links:
        if not isinstance(entry, dict):
            fail("matrix layout manifest contains a non-object link entry")
        source_identifier = entry.get("sourcePrefix")
        target_identifier = entry.get("targetPrefix")
        if source_identifier not in prefix_map or target_identifier not in prefix_map:
            fail("matrix layout manifest link references an unknown prefix")
        if source_identifier == target_identifier:
            fail("matrix layout manifest link must cross prefix identities")
        source_relative = safe_manifest_relative(entry.get("source"), "source")
        target_relative = safe_manifest_relative(entry.get("target"), "target")
        target_key = (str(target_identifier), target_relative.as_posix())
        if target_key in seen_targets:
            fail("matrix layout manifest contains duplicate target links")
        seen_targets.add(target_key)
        source_root = prefix_map[source_identifier]
        target_root = prefix_map[target_identifier]
        source_path = checked_manifest_node(source_root, source_relative, "source")
        target_path = checked_manifest_node(target_root, target_relative, "target")
        if predicate(source_path, source_root) is None or predicate(target_path, target_root) is None:
            fail("matrix layout manifest link escapes its declared policy")
        if source_path.is_symlink() or target_path.is_symlink():
            fail("matrix layout manifest link points to a symlink")
        try:
            source_info = source_path.stat()
            target_info = target_path.stat()
            if not stat.S_ISREG(source_info.st_mode) or not stat.S_ISREG(target_info.st_mode):
                fail("matrix layout manifest link is not a regular file")
            if node_constraints is not None:
                maximum = int(node_constraints["maxBytes"])
                if source_info.st_size > maximum or target_info.st_size > maximum:
                    fail("matrix layout manifest link exceeds its policy size limit")
                source_xattrs = read_supported_xattrs(source_path)
                target_xattrs = read_supported_xattrs(target_path)
                if source_xattrs != target_xattrs:
                    fail("matrix layout manifest link extended attributes changed")
                if node_constraints is not None and "xattrs" not in entry:
                    fail("matrix layout manifest link extended attributes are missing")
                if "xattrs" in entry and manifest_xattrs(entry.get("xattrs"), "matrix layout manifest link") != source_xattrs:
                    fail("matrix layout manifest link extended attributes changed")
            if not os.path.samefile(source_path, target_path):
                fail("matrix layout manifest link is no longer a hard link")
            if source_info.st_size != int(entry.get("size")):
                fail("matrix layout manifest link size changed")
            if digest(source_path) != entry.get("sha256"):
                fail("matrix layout manifest link content digest changed")
            expected_mode = int(str(entry.get("mode")), 8)
            if source_info.st_mode & 0o7777 != expected_mode or target_info.st_mode & 0o7777 != expected_mode:
                fail("matrix layout manifest link mode changed")
            if expected_mode & 0o222:
                fail("matrix layout manifest link is writable")
        except (OSError, TypeError, ValueError) as exception:
            fail(f"could not verify matrix layout manifest link: {exception}")
        declared_paths.add((str(source_identifier), source_relative.as_posix()))
        declared_paths.add((str(target_identifier), target_relative.as_posix()))

    if int(value.get("linkedFileCount", -1)) != len(links):
        fail("matrix layout manifest linked file count is inconsistent")
    if int(value.get("linkedBytes", -1)) != sum(int(entry["size"]) for entry in links):
        fail("matrix layout manifest linked byte count is inconsistent")
    audit_cross_prefix_hardlinks(prefix_map, declared_paths, predicate)
    return value


def verify_matrix_manifest(
    prefixes: list[tuple[str, Path]], manifest_path_value: Path
) -> dict:
    return verify_matrix_manifest_with_policy(
        prefixes,
        read_matrix_manifest(manifest_path_value),
        relative_immutable,
    )


def verify_static_runtime_matrix_manifest(
    prefixes: list[tuple[str, Path]], manifest_path_value: Path
) -> dict:
    return verify_matrix_manifest_with_policy(
        prefixes,
        read_static_runtime_matrix_manifest(manifest_path_value),
        relative_static_runtime,
        STATIC_RUNTIME_POLICY_DOCUMENT["nodeConstraints"],
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source")
    parser.add_argument("--target")
    parser.add_argument(
        "--prefix",
        action="append",
        help="matrix prefix in ID=/absolute/prefix form (repeat for each row)",
    )
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--freeze", action="store_true")
    parser.add_argument("--verify", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    try:
        options = parse_args(argv)
        if options.prefix:
            if options.source or options.target:
                fail("matrix --prefix cannot be combined with --source/--target")
            prefixes = [parse_matrix_prefix(value) for value in options.prefix]
            if len(prefixes) < 2:
                fail("matrix mode requires at least two --prefix values")
            manifest = matrix_manifest_path(options.manifest, create_parent=not options.verify)
            if options.verify:
                value = verify_matrix_manifest(prefixes, manifest)
            else:
                value = build_matrix_manifest(prefixes, manifest, options.freeze)
                write_manifest(manifest, value)
        else:
            if not options.source or not options.target:
                fail("--source and --target are required outside matrix mode")
            source = canonical_root(options.source, "source prefix")
            target = canonical_root(options.target, "target prefix")
            if source == target:
                fail("source and target prefixes must be different")
            manifest = manifest_path(options.manifest, create_parent=not options.verify)
            if options.verify:
                value = verify_manifest(source, target, manifest)
            else:
                value = build_manifest(source, target, manifest, options.freeze)
                write_manifest(manifest, value)
        print(
            f"wine-prefix-layout strategy={STRATEGY} links={value['linkedFileCount']} "
            f"bytes={value['linkedBytes']} verify={'true' if options.verify else 'false'}"
        )
        return 0
    except LayoutError as exception:
        print(f"wine-prefix-layout failed: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
