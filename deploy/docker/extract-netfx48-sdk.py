#!/usr/bin/env python3

import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import sys
import xml.etree.ElementTree as ET


EXPECTED_PRODUCT_CODE = "{949C0535-171C-480F-9CF4-D25C9E60FE88}"
EXPECTED_PRODUCT_VERSION = "4.8.03928"
EXPECTED_LANGUAGE = "1033"
EXPECTED_PAYLOADS = {
    "packages/netfxsdk/sdk_tools48.msi": "sdk_tools48.msi",
    "packages/netfxsdk/sdk_tools48.cab": "sdk_tools48.cab",
}
MAX_BUNDLE_FILES = 128
MAX_MANIFEST_BYTES = 1024 * 1024
SAFE_SOURCE_PATH = re.compile(r"[A-Za-z0-9._-]+")


class ExtractionError(Exception):
    pass


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def regular_file(path: Path) -> bool:
    try:
        return path.is_file() and not path.is_symlink()
    except OSError:
        return False


def find_burn_manifest(bundle_root: Path) -> ET.Element:
    files = sorted(bundle_root.iterdir(), key=lambda path: path.name)
    if len(files) > MAX_BUNDLE_FILES:
        raise ExtractionError("Developer Pack bundle contains too many payloads.")

    manifests: list[ET.Element] = []
    for path in files:
        if not regular_file(path):
            continue
        try:
            if path.stat().st_size > MAX_MANIFEST_BYTES:
                continue
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError, UnicodeDecodeError):
            continue
        if local_name(root.tag) == "BurnManifest":
            manifests.append(root)

    if len(manifests) != 1:
        raise ExtractionError("Developer Pack must contain exactly one Burn manifest.")
    return manifests[0]


def package_payloads(manifest: ET.Element) -> list[ET.Element]:
    packages = [
        element
        for element in manifest.iter()
        if local_name(element.tag) == "MsiPackage"
        and element.get("ProductCode", "").upper() == EXPECTED_PRODUCT_CODE
        and element.get("Version") == EXPECTED_PRODUCT_VERSION
        and element.get("Language") == EXPECTED_LANGUAGE
    ]
    if len(packages) != 1:
        raise ExtractionError("Developer Pack does not contain the exact .NET 4.8 SDK package.")

    payload_by_id: dict[str, ET.Element] = {}
    for element in manifest.iter():
        if local_name(element.tag) != "Payload":
            continue
        payload_id = element.get("Id")
        if not payload_id or payload_id in payload_by_id:
            raise ExtractionError("Developer Pack Burn payload identities are invalid.")
        payload_by_id[payload_id] = element

    references = [
        element.get("Id")
        for element in packages[0]
        if local_name(element.tag) == "PayloadRef"
    ]
    if any(reference is None for reference in references):
        raise ExtractionError("Developer Pack SDK payload reference is invalid.")
    try:
        payloads = [payload_by_id[reference] for reference in references]
    except KeyError as error:
        raise ExtractionError("Developer Pack SDK payload reference is missing.") from error

    actual_paths = {
        payload.get("FilePath", "").replace("\\", "/").lower()
        for payload in payloads
    }
    if len(payloads) != len(EXPECTED_PAYLOADS) or actual_paths != set(EXPECTED_PAYLOADS):
        raise ExtractionError("Developer Pack SDK payload closure is invalid.")
    return payloads


def verify_payload(bundle_root: Path, payload: ET.Element) -> tuple[Path, str]:
    file_path = payload.get("FilePath", "").replace("\\", "/").lower()
    output_name = EXPECTED_PAYLOADS[file_path]
    source_path = payload.get("SourcePath", "")
    if (
        payload.get("Packaging") != "embedded"
        or payload.get("Container") != "WixAttachedContainer"
        or not SAFE_SOURCE_PATH.fullmatch(source_path)
    ):
        raise ExtractionError("Developer Pack SDK payload mapping is invalid.")

    source = bundle_root / source_path
    if not regular_file(source):
        raise ExtractionError("Developer Pack SDK payload file is missing.")
    try:
        expected_size = int(payload.get("FileSize", ""))
    except ValueError as error:
        raise ExtractionError("Developer Pack SDK payload size is invalid.") from error
    if expected_size <= 0 or source.stat().st_size != expected_size:
        raise ExtractionError("Developer Pack SDK payload size is invalid.")

    expected_hash = payload.get("Hash", "").lower()
    if re.fullmatch(r"[0-9a-f]{40}", expected_hash):
        algorithm = "sha1"
    elif re.fullmatch(r"[0-9a-f]{64}", expected_hash):
        algorithm = "sha256"
    else:
        raise ExtractionError("Developer Pack SDK payload hash is invalid.")
    with source.open("rb") as stream:
        digest = hashlib.file_digest(stream, algorithm).hexdigest()
    if digest != expected_hash:
        raise ExtractionError("Developer Pack SDK payload hash does not match the Burn manifest.")
    return source, output_name


def extract(bundle_root: Path, output: Path) -> None:
    if not bundle_root.is_dir() or bundle_root.is_symlink():
        raise ExtractionError("Developer Pack bundle root is invalid.")
    if output.exists():
        if not output.is_dir() or output.is_symlink() or any(output.iterdir()):
            raise ExtractionError("Developer Pack SDK output directory must be empty.")
    else:
        output.mkdir(parents=True)

    manifest = find_burn_manifest(bundle_root)
    verified = [verify_payload(bundle_root, payload) for payload in package_payloads(manifest)]
    if {name for _, name in verified} != set(EXPECTED_PAYLOADS.values()):
        raise ExtractionError("Developer Pack SDK payload output names are invalid.")
    for source, name in verified:
        destination = output / name
        shutil.copyfile(source, destination)
        os.chmod(destination, 0o444)

    print(
        "netfx48-sdk-payloads status=ok "
        f"product={EXPECTED_PRODUCT_CODE} version={EXPECTED_PRODUCT_VERSION}"
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        extract(args.bundle_root, args.output)
    except (ExtractionError, OSError) as error:
        print(f"netfx48-sdk-payloads error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
