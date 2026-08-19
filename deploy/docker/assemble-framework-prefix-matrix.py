#!/usr/bin/env python3
"""Build and select the shared Wine/.NET Framework prefix matrix.

The Framework operator images are large mostly because every image carries a
complete Wine prefix.  This tool creates one parent filesystem containing the
exact rows below ``framework-prefixes/<targetId>`` and then asks the existing
``dedupe-wine-prefixes.py`` helper to hard-link only files accepted by the
versioned static runtime policy. Registry, user, cache, native-image, setup and
temporary state is copied per row and is never linked.

The input contract is deliberately filesystem based so a caller can use
``docker cp`` (or an equivalent audited image extractor) without giving this
tool Docker credentials.  A row directory has this shape::

    rows/netfx48/row.json
    rows/netfx48/clr4/<Wine prefix files>

``row.json`` binds the exact matrix identity.  The ``select`` operation is
used by a candidate Dockerfile: it verifies the parent manifest, removes the
unselected row directories (Docker records directory whiteouts), and creates
the one canonical CLR-generation symlink used by the selected profile.
"""

from __future__ import annotations

import argparse
import errno
import hashlib
import importlib.util
from importlib.machinery import SourceFileLoader
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath
from typing import Any


SCHEMA_VERSION = 1
MATRIX_STRATEGY = "shared-framework-target-prefix-matrix-v1"
SELECTOR_STRATEGY = "shared-framework-target-prefix-selector-v1"
INPUT_STRATEGY = "shared-framework-prefix-input-v1"
SAFE_ID_CHARS = frozenset("abcdefghijklmnopqrstuvwxyz0123456789._-")
KNOWN_PREFIXES = ("clr2", "clr4")
INSTALLER_NAMES = ("dotnetfx", "netfx", "ndp")
INSTALLED_FRAMEWORK_MEDIA = frozenset(("netfx.msi", "netfx1.cab"))
XATTR_POLICY = {
    "mode": "allowlisted-content-identity-v1",
    "allowedNames": ["user.DOSATTRIB"],
    "valueEncoding": "ascii-hex-mask",
    "maxValueBytes": 32,
    "outputNormalization": "strip-allowlisted-before-identity-v1",
}
TREE_FINGERPRINT_POLICY_DOCUMENT = {
    "schemaVersion": 1,
    "id": "complete-tree-metadata-v1",
    "nodes": ["directory", "regular-file", "symlink"],
    "metadata": ["type", "mode", "uid", "gid"],
    "regularFile": ["size", "sha256-content"],
    "symlink": ["link-target"],
    "xattrs": XATTR_POLICY,
    "specialNodes": "reject",
}
TREE_FINGERPRINT_POLICY_SHA256 = "sha256:" + hashlib.sha256(
    json.dumps(
        TREE_FINGERPRINT_POLICY_DOCUMENT,
        ensure_ascii=True,
        sort_keys=True,
        separators=(",", ":"),
    ).encode()
).hexdigest()


class MatrixError(RuntimeError):
    pass


def fail(message: str) -> "NoReturn":
    raise MatrixError(message)


def safe_id(value: object, label: str = "target id") -> str:
    if (
        not isinstance(value, str)
        or not value
        or len(value) > 128
        or value[0] not in "abcdefghijklmnopqrstuvwxyz0123456789"
        or any(character not in SAFE_ID_CHARS for character in value)
    ):
        fail(f"{label} is not a safe lowercase identifier")
    return value


def digest_pinned_image(value: object, label: str) -> str:
    if not isinstance(value, str) or not value or any(character.isspace() for character in value):
        fail(f"{label} must be a digest-pinned image reference")
    repository, separator, digest = value.rpartition("@")
    if (
        separator != "@"
        or not repository
        or "@" in repository
        or len(digest) != len("sha256:") + 64
        or not digest.startswith("sha256:")
        or any(character not in "0123456789abcdef" for character in digest[7:])
    ):
        fail(f"{label} must be repository@sha256:<64 lowercase hex>")
    return value


def absolute_directory(value: str, label: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        fail(f"{label} must be an absolute path")
    resolved = path.resolve(strict=False)
    if resolved != path:
        fail(f"{label} must not contain symlinked path components")
    if path == Path(path.anchor) or not path.is_dir():
        fail(f"{label} does not exist as a directory")
    return path


def absolute_output(value: str, label: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        fail(f"{label} must be an absolute path")
    if path.exists():
        if path.is_symlink() or not path.is_dir():
            fail(f"{label} must be a directory, not a symlink or file")
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.mkdir()
    return path


def load_dedupe_module(path: Path):
    if not path.is_file() or path.is_symlink():
        fail("dedupe helper is missing or is a symlink")
    # The production Dockerfile installs the helper under a stable extension-
    # free command name.  ``spec_from_file_location`` intentionally leaves
    # such paths without a loader, so select the source loader explicitly
    # after the regular path check.
    loader = SourceFileLoader("sharplabnext_dedupe", str(path))
    spec = importlib.util.spec_from_loader(loader.name, loader, origin=str(path))
    if spec is None:
        fail("could not load the dedupe helper")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def reject_prefix_artifacts(root: Path) -> None:
    """Reject files that should have been removed before parent assembly."""
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        for name in directories:
            if name.lower() in {"setupcache", "nativeimages", "cache", "caches"}:
                # Cache and native image directories are valid mutable state;
                # they must remain row-local, so do not reject them here.
                continue
        for name in files:
            lowered = name.lower()
            relative_parts = [part.lower() for part in (current_path / name).relative_to(root).parts]
            # CLR2's x64 installer leaves these two product-media files in its
            # registered Framework directory.  They are part of the installed
            # runtime state (and are present in the audited operator image),
            # unlike an installer staged at the prefix root or in a cache.
            if (
                lowered in INSTALLED_FRAMEWORK_MEDIA
                and "drive_c" in relative_parts
                and "windows" in relative_parts
                and "microsoft.net" in relative_parts
                and "framework64" in relative_parts
                and any("microsoft .net framework 2.0 (x64)" == part for part in relative_parts)
            ):
                continue
            if any(
                lowered.startswith(prefix) and lowered.endswith((".exe", ".msi", ".cab"))
                for prefix in INSTALLER_NAMES
            ):
                fail(f"private Framework installer artifact remains: {current_path / name}")


def validate_symlinks(root: Path) -> None:
    root = root.resolve(strict=True)
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        for name in [*directories, *files]:
            candidate = current_path / name
            if not candidate.is_symlink():
                continue
            try:
                target = candidate.resolve(strict=False)
            except OSError as exception:
                fail(f"could not resolve prefix symlink {candidate}: {exception}")
            # Wine creates the conventional drive-Z mapping to the host root.
            # It is required for Z:\\ paths used by the runner, but must remain
            # the only link allowed to leave the row.  All other links are
            # confined to the extracted prefix so a malformed operator input
            # cannot reach the parent filesystem.
            relative_candidate = candidate.relative_to(root)
            if (
                len(relative_candidate.parts) == 2
                and relative_candidate.parts[0].lower() == "dosdevices"
                and relative_candidate.parts[1].lower() == "z:"
                and os.readlink(candidate) == "/"
            ):
                continue
            try:
                target.relative_to(root)
            except ValueError:
                fail(f"prefix symlink escapes its row: {candidate}")


def validate_prefix(prefix: Path, label: str) -> None:
    prefix = absolute_directory(str(prefix), label)
    if not (prefix / "drive_c").is_dir():
        fail(f"{label} is not a Wine prefix (drive_c is missing)")
    registry = prefix / "system.reg"
    if not registry.is_file() or registry.is_symlink():
        fail(f"{label} is missing system.reg")
    try:
        first_lines = registry.read_text(encoding="utf-8", errors="strict").splitlines()[:32]
    except (OSError, UnicodeError) as exception:
        fail(f"{label} registry cannot be read: {exception}")
    if not any(line.strip() == "#arch=win64" for line in first_lines):
        fail(f"{label} registry does not declare #arch=win64")
    validate_symlinks(prefix)
    reject_prefix_artifacts(prefix)


def json_object(path: Path, label: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exception:
        fail(f"{label} is not valid UTF-8 JSON: {exception}")
    if not isinstance(value, dict):
        fail(f"{label} must be a JSON object")
    return value


def validate_row_metadata(row_id: str, path: Path) -> dict[str, Any]:
    if path.is_symlink() or not path.is_file():
        fail(f"row {row_id} metadata must be a regular file, not a symlink")
    value = json_object(path, f"row {row_id} metadata")
    if value.get("schemaVersion") != SCHEMA_VERSION:
        fail(f"row {row_id} metadata schemaVersion is unsupported")
    if value.get("id") != row_id:
        fail(f"row {row_id} metadata id does not match its directory")
    version = value.get("version")
    if not isinstance(version, str) or not version or len(version) > 32:
        fail(f"row {row_id} metadata version is invalid")
    generation = value.get("clrGeneration")
    if generation not in ("clr2", "clr4"):
        fail(f"row {row_id} metadata clrGeneration is invalid")
    target_prefix = value.get("targetPrefix", generation)
    if target_prefix not in KNOWN_PREFIXES:
        fail(f"row {row_id} metadata targetPrefix is invalid")
    if target_prefix != generation:
        fail(f"row {row_id} targetPrefix must match clrGeneration")
    companion = value.get("companionVersions", {})
    if not isinstance(companion, dict):
        fail(f"row {row_id} companionVersions must be an object")
    for key in KNOWN_PREFIXES:
        if key not in companion or not isinstance(companion[key], str) or not companion[key]:
            fail(f"row {row_id} companionVersions.{key} is required")
    if companion[generation] != version:
        fail(f"row {row_id} target companion version must equal its exact version")
    operator_image = digest_pinned_image(
        value.get("operatorImage"),
        f"row {row_id} operatorImage",
    )
    return {
        "schemaVersion": SCHEMA_VERSION,
        "id": row_id,
        "version": version,
        "clrGeneration": generation,
        "targetPrefix": target_prefix,
        "companionVersions": {key: companion[key] for key in KNOWN_PREFIXES},
        "operatorImage": operator_image,
    }


def rows_from_input(
    input_root: Path,
    *,
    metadata_only: bool = False,
) -> tuple[list[tuple[str, Path, dict[str, Any]]], str]:
    input_manifest_path = input_root / "matrix-input.json"
    if input_manifest_path.is_symlink() or not input_manifest_path.is_file():
        fail("matrix input must contain a regular matrix-input.json")
    input_manifest = json_object(input_manifest_path, "matrix-input.json")
    if input_manifest.get("schemaVersion") != SCHEMA_VERSION or input_manifest.get("strategy") != INPUT_STRATEGY:
        fail("matrix-input.json schema or strategy is unsupported")
    declared_rows = input_manifest.get("rows")
    if not isinstance(declared_rows, list) or not declared_rows:
        fail("matrix-input.json must declare rows")
    declared_by_id: dict[str, dict[str, Any]] = {}
    for declared in declared_rows:
        if not isinstance(declared, dict):
            fail("matrix-input.json contains a non-object row")
        row_id = safe_id(declared.get("id"), "matrix input row id")
        if row_id in declared_by_id:
            fail("matrix-input.json contains duplicate row IDs")
        declared_by_id[row_id] = declared
    rows_root = input_root / "rows"
    if not rows_root.is_dir() or rows_root.is_symlink():
        fail("matrix input must contain a real rows directory")
    rows: list[tuple[str, Path, dict[str, Any]]] = []
    for entry in sorted(rows_root.iterdir(), key=lambda item: item.name):
        if not entry.is_dir() or entry.is_symlink():
            fail(f"matrix rows contains a non-directory entry: {entry.name}")
        row_id = safe_id(entry.name)
        metadata = validate_row_metadata(row_id, entry / "row.json")
        declared = declared_by_id.get(row_id)
        if declared is None:
            fail(f"matrix-input.json is missing row {row_id}")
        for key in ("version", "clrGeneration", "targetPrefix", "companionVersions", "operatorImage"):
            if declared.get(key) != metadata.get(key):
                fail(f"matrix-input.json row {row_id} does not match row.json")
        if metadata_only:
            contents = sorted(child.name for child in entry.iterdir())
            if contents != ["row.json"]:
                fail(f"matrix metadata row {row_id} must contain only row.json")
        else:
            target_prefix = metadata["targetPrefix"]
            validate_prefix(entry / target_prefix, f"row {row_id} {target_prefix} prefix")
        rows.append((row_id, entry, metadata))
    if len(rows) < 2:
        fail("matrix input requires at least two exact Framework rows")
    if len({row[0] for row in rows}) != len(rows):
        fail("matrix row IDs must be unique")
    if set(declared_by_id) != {row[0] for row in rows}:
        fail("matrix-input.json rows do not match the rows directory")
    return rows, hashlib.sha256(input_manifest_path.read_bytes()).hexdigest()


def mounted_prefixes(
    values: list[str] | None,
    rows: list[tuple[str, Path, dict[str, Any]]],
) -> dict[tuple[str, str], Path]:
    if not values:
        return {}
    result: dict[tuple[str, str], Path] = {}
    expected = {
        (row_id, metadata["targetPrefix"])
        for row_id, _source_row, metadata in rows
    }
    for value in values:
        identity, separator, path_value = value.partition("=")
        row_id, identity_separator, prefix_name = identity.partition(":")
        key = (safe_id(row_id, "mounted prefix row id"), prefix_name)
        if separator != "=" or identity_separator != ":" or prefix_name not in KNOWN_PREFIXES:
            fail("mounted prefix must use ROW_ID:clr2|clr4=/absolute/path")
        if key in result:
            fail(f"mounted prefix {row_id}:{prefix_name} is duplicated")
        prefix = absolute_directory(path_value, f"mounted prefix {row_id}:{prefix_name}")
        validate_prefix(prefix, f"mounted prefix {row_id}:{prefix_name}")
        result[key] = prefix
    missing = sorted(expected - set(result))
    unexpected = sorted(set(result) - expected)
    if missing or unexpected:
        fail(
            "mounted prefixes do not match matrix metadata "
            f"(missing={missing}, unexpected={unexpected})"
        )
    return result


def mounted_prefix_root(
    value: str | None,
    rows: list[tuple[str, Path, dict[str, Any]]],
) -> dict[tuple[str, str], Path]:
    if value is None:
        return {}
    root = absolute_directory(value, "mounted prefix root")
    expected_ids = sorted(row_id for row_id, _source_row, _metadata in rows)
    metadata_by_id = {
        row_id: metadata for row_id, _source_row, metadata in rows
    }
    actual_ids = sorted(entry.name for entry in root.iterdir())
    if actual_ids != expected_ids:
        fail("mounted prefix root rows do not match matrix metadata")
    result: dict[tuple[str, str], Path] = {}
    for row_id in expected_ids:
        row_root = root / row_id
        if row_root.is_symlink() or not row_root.is_dir():
            fail(f"mounted prefix row {row_id} is not a real directory")
        metadata = metadata_by_id[row_id]
        target_prefix = metadata["targetPrefix"]
        actual_prefixes = sorted(entry.name for entry in row_root.iterdir())
        if actual_prefixes != [target_prefix]:
            fail(f"mounted prefix row {row_id} must contain only {target_prefix}")
        prefix = row_root / target_prefix
        validate_prefix(prefix, f"mounted prefix {row_id}:{target_prefix}")
        result[(row_id, target_prefix)] = prefix
    return result


def copy_tree(source: Path, destination: Path) -> None:
    if destination.exists() or destination.is_symlink():
        fail(f"matrix output already contains {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copytree(source, destination, symlinks=True, copy_function=shutil.copy2)
    except (OSError, shutil.Error) as exception:
        fail(f"could not copy Framework prefix {source}: {exception}")


def canonical_json(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n").encode()


def tree_fingerprint_policy_identity() -> dict[str, str]:
    return {
        "id": TREE_FINGERPRINT_POLICY_DOCUMENT["id"],
        "sha256": TREE_FINGERPRINT_POLICY_SHA256,
    }


def file_sha256(path: Path, label: str) -> str:
    if path.is_symlink() or not path.is_file():
        fail(f"{label} must be a regular file, not a symlink")
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            while chunk := stream.read(1024 * 1024):
                digest.update(chunk)
    except OSError as exception:
        fail(f"could not hash {label}: {exception}")
    return digest.hexdigest()


def read_supported_xattrs(path: Path) -> tuple[tuple[str, bytes], ...]:
    listxattr = getattr(os, "listxattr", None)
    if listxattr is None:
        return ()
    try:
        attributes = listxattr(path, follow_symlinks=False)
    except TypeError:
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
    result: list[tuple[str, bytes]] = []
    for attribute in attributes:
        name = attribute.decode("utf-8", "strict") if isinstance(attribute, bytes) else attribute
        if name not in set(XATTR_POLICY["allowedNames"]):
            fail(f"Framework prefix node has unsupported extended attributes: {path}")
        try:
            value = os.getxattr(path, attribute, follow_symlinks=False)
        except OSError as exception:
            fail(f"could not read extended attribute {name} for {path}: {exception}")
        validate_xattr_value(name, value, f"Framework prefix node extended attribute on {path}")
        result.append((name, bytes(value)))
    return tuple(sorted(result, key=lambda item: (item[0], item[1])))


def validate_xattr_value(name: str, value: bytes, label: str) -> None:
    """Validate the allowlisted xattr contract used in tree fingerprints."""
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


def encoded_xattrs(value: tuple[tuple[str, bytes], ...]) -> bytes:
    return json.dumps(
        [{"name": name, "value": data.hex()} for name, data in value],
        ensure_ascii=True,
        sort_keys=True,
        separators=(",", ":"),
    ).encode()


def tree_fingerprint(root: Path) -> str:
    fingerprint = hashlib.sha256()

    def visit(path: Path, relative: PurePosixPath) -> None:
        try:
            info = path.lstat()
        except OSError as exception:
            fail(f"could not inspect Framework prefix node {path}: {exception}")
        xattrs = encoded_xattrs(read_supported_xattrs(path))
        relative_bytes = os.fsencode("." if not relative.parts else relative.as_posix())
        metadata = (
            b"\0".join(
                (
                    relative_bytes,
                    f"{info.st_mode & 0o7777:04o}".encode(),
                    str(info.st_uid).encode(),
                    str(info.st_gid).encode(),
                    str(len(xattrs)).encode(),
                    xattrs,
                )
            )
            + b"\0"
        )
        if stat.S_ISDIR(info.st_mode):
            fingerprint.update(b"D\0" + metadata + b"\n")
            try:
                children = sorted(os.scandir(path), key=lambda entry: entry.name)
            except OSError as exception:
                fail(f"could not enumerate Framework prefix directory {path}: {exception}")
            for child in children:
                child_relative = relative / child.name if relative.parts else PurePosixPath(child.name)
                visit(Path(child.path), child_relative)
            return
        if stat.S_ISLNK(info.st_mode):
            try:
                target = os.fsencode(os.readlink(path))
            except OSError as exception:
                fail(f"could not read Framework prefix symlink {path}: {exception}")
            fingerprint.update(
                b"L\0" + metadata + str(len(target)).encode() + b"\0" + target + b"\n"
            )
            return
        if stat.S_ISREG(info.st_mode):
            fingerprint.update(b"F\0" + metadata + str(info.st_size).encode() + b"\0")
            try:
                with path.open("rb") as stream:
                    while chunk := stream.read(1024 * 1024):
                        fingerprint.update(chunk)
            except OSError as exception:
                fail(f"could not hash Framework prefix file {path}: {exception}")
            fingerprint.update(b"\n")
            return
        fail(f"Framework prefix contains an unsupported special node: {path}")

    visit(root, PurePosixPath())
    return fingerprint.hexdigest()


def validate_matrix_shape(
    root: Path,
    manifest: dict[str, Any],
    selected_id: str | None = None,
) -> None:
    rows = {
        row["id"]: row
        for row in manifest["rows"]
        if selected_id is None or row["id"] == selected_id
    }
    expected_ids = sorted(rows)
    actual_ids = sorted(entry.name for entry in root.iterdir())
    if actual_ids != expected_ids:
        fail(
            "framework matrix root rows do not exactly match its manifest "
            f"(expected={expected_ids}, actual={actual_ids})"
        )
    for row_id, row in rows.items():
        row_root = root / row_id
        try:
            row_info = row_root.lstat()
        except OSError as exception:
            fail(f"could not inspect framework matrix row {row_id}: {exception}")
        if not stat.S_ISDIR(row_info.st_mode):
            fail(f"framework matrix row {row_id} is not a real directory")
        target_prefix = row["targetPrefix"]
        actual_prefixes = sorted(entry.name for entry in row_root.iterdir())
        if actual_prefixes != [target_prefix]:
            fail(f"framework matrix row {row_id} must contain only {target_prefix}")
        prefix = row_root / target_prefix
        try:
            prefix_info = prefix.lstat()
        except OSError as exception:
            fail(f"could not inspect framework matrix prefix {row_id}/{target_prefix}: {exception}")
        if not stat.S_ISDIR(prefix_info.st_mode):
            fail(f"framework matrix prefix {row_id}/{target_prefix} is not a real directory")


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(mode="wb", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(canonical_json(value))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except OSError as exception:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
        fail(f"could not write matrix manifest: {exception}")


def run_preflight(command: str | None, prefix: Path, version: str) -> None:
    if command is None:
        return
    if not Path(command).is_file() or Path(command).is_symlink():
        fail("preflight command is missing or is a symlink")
    try:
        result = subprocess.run(
            [command, str(prefix), version],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            check=False,
            timeout=300,
            env={
                **os.environ,
                "SHARPLABNEXT_FRAMEWORK_MATRIX_PREFLIGHT": "1",
            },
        )
    except (OSError, subprocess.SubprocessError) as exception:
        fail(f"Framework prefix preflight could not run: {exception}")
    if result.returncode != 0:
        fail(f"Framework prefix preflight rejected {prefix} as {version}")


def assemble(options: argparse.Namespace) -> int:
    input_root = absolute_directory(options.input, "matrix input")
    output_root = absolute_output(options.output, "matrix output")
    if output_root == input_root or output_root in input_root.parents or input_root in output_root.parents:
        fail("matrix input and output must be disjoint")
    if any(output_root.iterdir()):
        fail("matrix output must be empty")
    if options.row_prefix and options.row_prefix_root:
        fail("--row-prefix and --row-prefix-root cannot be combined")
    use_mounted_prefixes = bool(options.row_prefix or options.row_prefix_root)
    rows, input_manifest_sha256 = rows_from_input(
        input_root,
        metadata_only=use_mounted_prefixes,
    )
    mounted = (
        mounted_prefix_root(options.row_prefix_root, rows)
        if options.row_prefix_root
        else mounted_prefixes(options.row_prefix, rows)
    )
    dedupe_path = Path(options.dedupe_helper).resolve(strict=False)
    dedupe = load_dedupe_module(dedupe_path)

    matrix_root = output_root / "framework-prefixes"
    matrix_root.mkdir(parents=True)
    prefix_arguments: list[tuple[str, Path]] = []
    layout_builder = dedupe.StaticRuntimeMatrixManifestBuilder(freeze=True)
    row_records: list[dict[str, Any]] = []
    for row_id, source_row, metadata in rows:
        destination_row = matrix_root / row_id
        destination_row.mkdir()
        target_prefix = metadata["targetPrefix"]
        row_records.append(
            {
                **metadata,
                "prefixes": {target_prefix: f"{row_id}/{target_prefix}"},
                "rowDigest": "pending",
            }
        )
        destination = destination_row / target_prefix
        source_prefix = mounted.get((row_id, target_prefix), source_row / target_prefix)
        copy_tree(source_prefix, destination)
        run_preflight(options.preflight_command, destination, metadata["version"])
        # BuildKit exposes DOSATTRIB bytes from the operator snapshot, while
        # OCI export does not preserve them. Validate first, then normalize the
        # final payload before any cross-row link or content identity is made.
        dedupe.normalize_supported_xattrs(destination)
        prefix_identifier = f"{row_id}--{target_prefix}"
        prefix_arguments.append((prefix_identifier, destination))
        # Link each copied target immediately. This prevents the build layer
        # from holding all raw rows before the final dedupe pass.
        layout_builder.add_prefix(prefix_identifier, destination)

    validate_matrix_shape(matrix_root, {"rows": row_records})
    # The helper owns the versioned positive policy. Its matrix mode links only
    # identical static runtime files and freezes them.
    layout_path = output_root / ".wine-prefix-layout.json"
    layout = layout_builder.manifest()
    dedupe.write_manifest(layout_path, layout)
    dedupe.verify_static_runtime_matrix_manifest(prefix_arguments, layout_path)

    for record in row_records:
        row_id = record["id"]
        record["rowDigest"] = hashlib.sha256(
            canonical_json(
                {
                    **record,
                    "rowDigest": None,
                }
            )
            + tree_fingerprint(matrix_root / row_id).encode()
        ).hexdigest()

    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "strategy": MATRIX_STRATEGY,
        "prefixRoot": "/opt/sharplabnext/framework-prefixes",
        "layoutManifest": ".wine-prefix-layout.json",
        "inputManifestSha256": f"sha256:{input_manifest_sha256}",
        "dedupePolicy": dedupe.static_runtime_policy_identity(),
        "treeFingerprintPolicy": tree_fingerprint_policy_identity(),
        "freeze": True,
        "rows": sorted(row_records, key=lambda row: row["id"]),
        "layout": {
            "strategy": layout["strategy"],
            "policy": layout["policy"],
            "linkedFileCount": layout["linkedFileCount"],
            "linkedBytes": layout["linkedBytes"],
        },
    }
    write_json(output_root / "framework-matrix.json", manifest)
    return len(rows)


def read_matrix_manifest(path: Path) -> dict[str, Any]:
    if path.is_symlink():
        fail("framework matrix manifest must not be a symlink")
    value = json_object(path, "framework matrix manifest")
    if value.get("schemaVersion") != SCHEMA_VERSION or value.get("strategy") != MATRIX_STRATEGY:
        fail("framework matrix manifest schema or strategy is unsupported")
    if value.get("prefixRoot") != "/opt/sharplabnext/framework-prefixes":
        fail("framework matrix manifest prefixRoot is invalid")
    if value.get("layoutManifest") != ".wine-prefix-layout.json":
        fail("framework matrix manifest layoutManifest is invalid")
    input_digest = value.get("inputManifestSha256")
    if (
        not isinstance(input_digest, str)
        or len(input_digest) != len("sha256:") + 64
        or not input_digest.startswith("sha256:")
        or any(character not in "0123456789abcdef" for character in input_digest[7:])
    ):
        fail("framework matrix manifest inputManifestSha256 is invalid")
    policy = value.get("dedupePolicy")
    if (
        not isinstance(policy, dict)
        or policy.get("id") != "wine-static-runtime-payload-v1"
        or not isinstance(policy.get("sha256"), str)
        or not policy["sha256"].startswith("sha256:")
        or len(policy["sha256"]) != len("sha256:") + 64
        or any(character not in "0123456789abcdef" for character in policy["sha256"][7:])
    ):
        fail("framework matrix manifest dedupe policy is invalid")
    if value.get("treeFingerprintPolicy") != tree_fingerprint_policy_identity():
        fail("framework matrix manifest tree fingerprint policy is invalid")
    if value.get("freeze") is not True:
        fail("framework matrix manifest must declare frozen immutable links")
    layout = value.get("layout")
    if not isinstance(layout, dict) or layout.get("strategy") != "hardlink-static-runtime-matrix-v1":
        fail("framework matrix manifest layout strategy is invalid")
    if layout.get("policy") != policy:
        fail("framework matrix manifest layout policy is invalid")
    for field in ("linkedFileCount", "linkedBytes"):
        count = layout.get(field)
        if not isinstance(count, int) or isinstance(count, bool) or count < 0:
            fail(f"framework matrix manifest layout {field} is invalid")
    rows = value.get("rows")
    if not isinstance(rows, list) or len(rows) < 2:
        fail("framework matrix manifest must contain at least two rows")
    ids = []
    for row in rows:
        if not isinstance(row, dict):
            fail("framework matrix manifest contains a non-object row")
        row_id = safe_id(row.get("id"))
        ids.append(row_id)
        target_prefix = row.get("targetPrefix")
        if target_prefix not in KNOWN_PREFIXES:
            fail(f"framework matrix row {row_id} target prefix is invalid")
        prefixes = row.get("prefixes")
        if prefixes != {target_prefix: f"{row_id}/{target_prefix}"}:
            fail(f"framework matrix row {row_id} prefixes are invalid")
        if (
            not isinstance(row.get("rowDigest"), str)
            or len(row["rowDigest"]) != 64
            or any(character not in "0123456789abcdef" for character in row["rowDigest"])
        ):
            fail(f"framework matrix row {row_id} digest is invalid")
    if ids != sorted(ids) or len(set(ids)) != len(ids):
        fail("framework matrix row IDs must be sorted and unique")
    return value


def verify_row_digests(root: Path, manifest: dict[str, Any]) -> None:
    """Recompute every row identity before selector mutation.

    The layout manifest proves that the immutable hard-link set is intact, but
    it deliberately excludes mutable registry/user/cache files.  The row
    digest covers the complete row tree and metadata, so checking it here
    prevents a post-assembly edit from being hidden by deleting that row.
    """
    for row in manifest["rows"]:
        row_id = row["id"]
        row_root = root / row_id
        if not row_root.is_dir() or row_root.is_symlink():
            fail(f"framework matrix row {row_id} is missing or is not a directory")
        expected = row["rowDigest"]
        actual = hashlib.sha256(
            canonical_json({**row, "rowDigest": None})
            + tree_fingerprint(row_root).encode()
        ).hexdigest()
        if actual != expected:
            fail(f"framework matrix row {row_id} content does not match its recorded digest")


def verify_matrix(
    root: Path,
    expected_input_manifest_sha256: str,
    dedupe_helper: str,
) -> tuple[dict[str, Any], str, str]:
    manifest_path = root.parent / "framework-matrix.json"
    parent_manifest_sha256 = file_sha256(manifest_path, "framework matrix manifest")
    manifest = read_matrix_manifest(manifest_path)
    if manifest["inputManifestSha256"] != expected_input_manifest_sha256:
        fail("framework matrix input manifest digest does not match the selected candidate")
    validate_matrix_shape(root, manifest)
    layout_path = root.parent / str(manifest["layoutManifest"])
    layout_manifest_sha256 = file_sha256(layout_path, "framework layout manifest")
    dedupe_path = Path(dedupe_helper).resolve(strict=False)
    dedupe = load_dedupe_module(dedupe_path)
    prefix_arguments = []
    for row in manifest["rows"]:
        target_prefix = row["targetPrefix"]
        prefix_arguments.append(
            (
                f"{row['id']}--{target_prefix}",
                root / row["prefixes"][target_prefix],
            )
        )
    expected_policy = dedupe.static_runtime_policy_identity()
    if manifest["dedupePolicy"] != expected_policy:
        fail("framework matrix manifest dedupe policy does not match this helper")
    verified_layout = dedupe.verify_static_runtime_matrix_manifest(
        prefix_arguments,
        layout_path,
    )
    manifest_layout = manifest["layout"]
    if (
        verified_layout.get("strategy") != manifest_layout["strategy"]
        or verified_layout.get("policy") != manifest_layout["policy"]
        or verified_layout.get("linkedFileCount") != manifest_layout["linkedFileCount"]
        or verified_layout.get("linkedBytes") != manifest_layout["linkedBytes"]
    ):
        fail("framework matrix manifest layout summary does not match its layout manifest")
    # Verify all rows before removing hidden rows or creating canonical links.
    # This keeps the selector receipt bound to the exact parent filesystem.
    verify_row_digests(root, manifest)
    validate_matrix_shape(root, manifest)
    if file_sha256(manifest_path, "framework matrix manifest") != parent_manifest_sha256:
        fail("framework matrix manifest changed while it was being verified")
    if file_sha256(layout_path, "framework layout manifest") != layout_manifest_sha256:
        fail("framework layout manifest changed while it was being verified")
    return manifest, parent_manifest_sha256, layout_manifest_sha256


def verify(options: argparse.Namespace) -> int:
    root = absolute_directory(options.root, "matrix root")
    manifest, _parent_manifest_sha256, _layout_manifest_sha256 = verify_matrix(
        root,
        options.expected_input_manifest_sha256,
        options.dedupe_helper,
    )
    return len(manifest["rows"])


def select(options: argparse.Namespace) -> int:
    root = absolute_directory(options.root, "matrix root")
    parent_image = digest_pinned_image(options.expected_parent_image, "expected parent image")
    manifest, parent_digest, layout_digest = verify_matrix(
        root,
        options.expected_input_manifest_sha256,
        options.dedupe_helper,
    )
    target_id = safe_id(options.target_id, "selected target id")
    rows = {row["id"]: row for row in manifest["rows"]}
    if target_id not in rows:
        fail(f"selected target {target_id} is not in the framework matrix")

    selected = rows[target_id]
    if selected.get("operatorImage") != options.expected_operator_image:
        fail("selected Framework row operator image does not match the candidate")
    if f"sha256:{selected['rowDigest']}" != options.expected_row_digest:
        fail("selected Framework row digest does not match the candidate")
    if options.expected_version is not None and selected.get("version") != options.expected_version:
        fail("selected Framework row version does not match the candidate")
    if options.expected_generation is not None and selected.get("clrGeneration") != options.expected_generation:
        fail("selected Framework row CLR generation does not match the candidate")
    selected_row_root = root / target_id
    if not selected_row_root.is_dir() or selected_row_root.is_symlink():
        fail("selected Framework row directory is missing")
    target_prefix = selected["targetPrefix"]
    canonical = Path(options.canonical_prefix)
    if not canonical.is_absolute() or canonical == Path(canonical.anchor):
        fail("canonical prefix path is invalid")
    if canonical.name != f"wine-netfx-{target_prefix}":
        fail("canonical prefix path does not match the selected CLR generation")
    resolved_parent = canonical.parent.resolve(strict=False)
    if resolved_parent == root or root in resolved_parent.parents:
        fail("canonical prefix path must be outside the matrix root")
    receipt_path = Path(options.receipt)
    if not receipt_path.is_absolute():
        fail("selector receipt must be an absolute path")
    receipt_parent = receipt_path.parent.resolve(strict=False)
    if receipt_parent == root or root in receipt_parent.parents:
        fail("selector receipt must be outside the matrix root")

    hidden_paths: list[Path] = []
    for row_id in rows:
        if row_id == target_id:
            continue
        candidate = root / row_id
        if candidate.is_symlink() or not candidate.is_dir():
            fail(f"framework row path is not a real directory: {row_id}")
        hidden_paths.append(candidate)
    for candidate in hidden_paths:
        shutil.rmtree(candidate)
    validate_matrix_shape(root, manifest, selected_id=target_id)

    if canonical.exists() or canonical.is_symlink():
        if canonical.is_symlink() or canonical.is_dir():
            shutil.rmtree(canonical) if canonical.is_dir() and not canonical.is_symlink() else canonical.unlink()
        else:
            canonical.unlink()
    canonical.parent.mkdir(parents=True, exist_ok=True)
    os.symlink(selected_row_root / target_prefix, canonical, target_is_directory=True)
    validate_matrix_shape(root, manifest, selected_id=target_id)

    receipt = {
        "schemaVersion": SCHEMA_VERSION,
        "strategy": SELECTOR_STRATEGY,
        "targetId": target_id,
        "parentImage": parent_image,
        "parentManifestSha256": parent_digest,
        "layoutManifestSha256": layout_digest,
        "selectedRowDigest": selected["rowDigest"],
        "selectedOperatorImage": selected["operatorImage"],
        "hiddenRows": sorted(row_id for row_id in rows if row_id != target_id),
        "targetPrefix": target_prefix,
        "canonicalPrefix": str(canonical),
        "whiteoutMode": "directory",
    }
    write_json(receipt_path, receipt)
    return 0


def parser() -> argparse.ArgumentParser:
    value = argparse.ArgumentParser(add_help=False)
    root = argparse.ArgumentParser(description=__doc__)
    sub = root.add_subparsers(dest="operation", required=True)

    assemble_parser = sub.add_parser("assemble", parents=[value])
    assemble_parser.add_argument("--input", required=True)
    assemble_parser.add_argument("--output", required=True)
    assemble_parser.add_argument("--dedupe-helper", required=True)
    assemble_parser.add_argument("--preflight-command")
    assemble_parser.add_argument(
        "--row-prefix",
        action="append",
        help="read-only mounted target prefix in ROW_ID:clr2|clr4=/absolute/path form",
    )
    assemble_parser.add_argument(
        "--row-prefix-root",
        help="root containing exactly one ROW_ID/targetPrefix read-only mount per row",
    )
    assemble_parser.set_defaults(handler=assemble)

    select_parser = sub.add_parser("select", parents=[value])
    select_parser.add_argument("--root", required=True)
    select_parser.add_argument("--target-id", required=True)
    select_parser.add_argument("--canonical-prefix", required=True)
    select_parser.add_argument("--receipt", required=True)
    select_parser.add_argument("--dedupe-helper", required=True)
    select_parser.add_argument("--expected-input-manifest-sha256", required=True)
    select_parser.add_argument("--expected-parent-image", required=True)
    select_parser.add_argument("--expected-operator-image", required=True)
    select_parser.add_argument("--expected-row-digest", required=True)
    select_parser.add_argument("--expected-version")
    select_parser.add_argument("--expected-generation", choices=("clr2", "clr4"))
    select_parser.set_defaults(handler=select)

    verify_parser = sub.add_parser("verify", parents=[value])
    verify_parser.add_argument("--root", required=True)
    verify_parser.add_argument("--dedupe-helper", required=True)
    verify_parser.add_argument("--expected-input-manifest-sha256", required=True)
    verify_parser.set_defaults(handler=verify)
    return root


def main(argv: list[str]) -> int:
    try:
        options = parser().parse_args(argv)
        result = options.handler(options)
        if options.operation == "assemble":
            print(f"framework-prefix-matrix rows={result} strategy={MATRIX_STRATEGY}")
        elif options.operation == "select":
            print(f"framework-prefix-selector target={options.target_id} strategy={SELECTOR_STRATEGY}")
        else:
            print(f"framework-prefix-matrix verified-rows={result} strategy={MATRIX_STRATEGY}")
        return 0
    except MatrixError as exception:
        print(f"framework-prefix-matrix failed: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
