"""Freeze the approved GTA presentation sources; never opens game/profile executables.

Reference bytes remain REFERENCE_VERIFIED. Localization comes from a previously
acquired, digest-pinned Rockstar language container. All output is a new bundle.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import urllib.request
from datetime import datetime
from pathlib import Path, PurePosixPath

MANIFEST = Path(__file__).with_name("gta_v_enhanced_presentation_source_families.v1.json")
RECEIPT_NAME = "gta-v-enhanced-presentation-acquisition-receipt.v1.json"
MAX_REFERENCE_BYTES = 40 * 1024 * 1024


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def strict_json(data: bytes) -> dict:
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise ValueError("Duplicate JSON property")
            result[key] = value
        return result
    value = json.loads(data.decode("utf-8", "strict"), object_pairs_hook=pairs)
    if not isinstance(value, dict):
        raise ValueError("Expected an object")
    return value


def relative_path(value: str) -> str:
    p = PurePosixPath(value)
    if not value or p.is_absolute() or str(p) != value or any(x in ("..", ".") for x in p.parts) or "\\" in value or ":" in value:
        raise ValueError("Invalid relative path")
    return value


def load_manifest(path: Path = MANIFEST) -> dict:
    value = strict_json(path.read_bytes())
    if (value.get("schemaVersion"), value.get("manifestId"), value.get("gameId"), value.get("steamAppId"), value.get("steamBuildId")) != (
            1, "grid.gta-v-enhanced.presentation-source-families", "game.grandtheftautov-enhanced", "3240220", "25261616"):
        raise ValueError("Wrong presentation manifest identity")
    ids = set()
    for reference in value["references"]:
        if reference.get("url"):
            if reference["url"] != "https://gtamods.com/mediawiki/index.php?title=Vehicles.meta&oldid=19769#vehicleClass" or reference["revision"] != "19769" or reference["id"] in ids or not re.fullmatch(r"[0-9a-f]{64}", reference["sha256"]):
                raise ValueError("Unapproved reference snapshot")
            relative_path(reference["path"])
            ids.add(reference["id"])
            continue
        if reference["id"] in ids or not re.fullmatch(r"[0-9a-f]{40}", reference["revision"]) or not re.fullmatch(r"[0-9a-f]{64}", reference["sha256"]):
            raise ValueError("Invalid or duplicate reference pin")
        if reference["repository"] not in ("root-cause/v-decompiled-scripts", "citizenfx/natives", "DurtyFree/gta-v-data-dumps"):
            raise ValueError("Unapproved reference repository")
        relative_path(reference["path"])
        ids.add(reference["id"])
    return value


def reference_coordinate(reference: dict) -> str:
    if "url" in reference:
        return reference["url"]
    return "https://raw.githubusercontent.com/" + reference["repository"] + "/" + reference["revision"] + "/" + reference["path"]


def checked(data: bytes, expected: str, name: str) -> bytes:
    if len(data) > MAX_REFERENCE_BYTES or sha(data) != expected:
        raise ValueError("Digest/size mismatch: " + name)
    return data


def acquire(language_container: Path, output: Path, observed: str, references_directory: Path | None = None, manifest_path: Path = MANIFEST) -> Path:
    manifest = load_manifest(manifest_path)
    stamp = datetime.fromisoformat(observed.replace("Z", "+00:00"))
    if stamp.tzinfo is None or stamp.utcoffset().total_seconds() != 0:
        raise ValueError("Observation must be explicit UTC")
    if output.exists():
        raise ValueError("Presentation output must be a new directory")
    container = checked(language_container.read_bytes(), manifest["languageContainer"]["sha256"], "language container")
    # Dependency is intentionally lazy so safety/receipt tests do not load FiveFury.
    from fivefury.rpf import RpfArchive, RpfFileEntry
    archive = RpfArchive.from_bytes(container, name="american_rel.rpf")
    artifacts = []
    frozen = []
    try:
        for member in manifest["languageContainer"]["members"]:
            entry = archive.find_entry(member["path"])
            if not isinstance(entry, RpfFileEntry):
                raise ValueError("Missing exact localization member")
            data = checked(archive.read_entry_bytes(entry, logical=True), member["sha256"], member["path"])
            frozen.append(("members/" + relative_path(member["path"]), data))
            artifacts.append(dict(coordinate=manifest["languageContainer"]["coordinate"] + "!/" + member["path"], relativePath=frozen[-1][0], sha256=sha(data), byteLength=len(data), evidenceClass="FILE_VERIFIED", formatId="rockstar.gta-v.gxt2-binary"))
    finally:
        archive.close()
    for ref in manifest["references"]:
        coordinate = reference_coordinate(ref)
        if references_directory is None:
            with urllib.request.urlopen(coordinate, timeout=30) as response:
                if response.url != coordinate:
                    raise ValueError("Reference acquisition redirected")
                data = response.read(MAX_REFERENCE_BYTES + 1)
        else:
            root = references_directory.resolve(strict=True)
            candidate = root.joinpath(*PurePosixPath(relative_path(ref["path"])).parts).resolve(strict=True)
            if root not in candidate.parents:
                raise ValueError("Reference escaped root")
            data = candidate.read_bytes()
        checked(data, ref["sha256"], ref["id"])
        data.decode("utf-8", "strict")
        frozen.append(("references/" + relative_path(ref["path"]), data))
        artifacts.append(dict(coordinate=coordinate, relativePath=frozen[-1][0], sha256=sha(data), byteLength=len(data), evidenceClass="REFERENCE_VERIFIED", formatId="grid.gta-v.presentation-reference-utf8"))
    receipt = dict(schemaVersion=1, schemaId="grid.gta-v.presentation-acquisition", gameId=manifest["gameId"], gameVersion=manifest["steamBuildId"], observedAtUtc=observed,
                   manifestSha256=sha(canonical(manifest)), acquisitionScriptSha256=sha(Path(__file__).read_bytes()),
                   languageContainer=manifest["languageContainer"]["coordinate"], languageContainerSha256=sha(container), languageContainerByteLength=len(container),
                   artifacts=sorted(artifacts, key=lambda x: x["coordinate"]))
    receipt["contentSha256"] = sha(canonical(receipt))
    output.mkdir(parents=True)
    for relative, data in frozen:
        destination = output.joinpath(*PurePosixPath(relative).parts)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    path = output / RECEIPT_NAME
    path.write_bytes(canonical(receipt))
    return path


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--language-container", required=True, type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--observed-at-utc", required=True)
    p.add_argument("--references-directory", type=Path)
    args = p.parse_args()
    print(acquire(args.language_container, args.output, args.observed_at_utc, args.references_directory))


if __name__ == "__main__":
    main()
