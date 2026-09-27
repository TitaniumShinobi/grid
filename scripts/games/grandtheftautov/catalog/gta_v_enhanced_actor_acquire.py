"""Read-only mounted-ped acquisition for the verified GTA V Enhanced build.

This preproduction boundary is deliberately separate from the historical
four-container acquisition receipt.  It closes the Rockstar DLC mount graph,
freezes exact ped-registry bytes, and emits a deterministic derived index.  It
has no GRID runtime authority and never records local paths or credentials.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import re
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime
from pathlib import Path, PurePosixPath
from typing import Any

from fivefury.crypto import GameCrypto
from fivefury.rpf import RpfArchive, RpfFileEntry
from fivefury.ymt import read_ped_metadata


SCHEMA_VERSION = 1
INDEX_SCHEMA_VERSION = 1
GAME_ID = "game.grandtheftautov-enhanced"
STEAM_APP_ID = "3240220"
LOCK_PATH = Path(__file__).with_name("fivefury.lock.v1.json")
MANIFEST_PATH = Path(__file__).with_name(
    "gta_v_enhanced_actor_source_families.v1.json"
)
EXPECTED_MANIFEST_SHA256 = (
    "63fe8c6a97c011cb70a62e0f88e22bcb1e42fb70fcac86dbb7d450aebc3c50ce"
)
RECEIPT_NAME = "gta-v-enhanced-actor-acquisition-receipt.v1.json"
INDEX_NAME = "actor-corpus-index.v1.json"
MAX_XML_BYTES = 16 * 1024 * 1024


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8", "strict")


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _sha256_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    length = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            length += len(chunk)
            digest.update(chunk)
    return length, digest.hexdigest()


def _strict_text(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value or value.strip() != value:
        raise ValueError(f"{name} must be nonempty exact text without surrounding whitespace")
    value.encode("utf-8", "strict")
    return value


def _lower_sha256(value: Any, name: str) -> str:
    value = _strict_text(value, name)
    if not re.fullmatch(r"[0-9a-f]{64}", value):
        raise ValueError(f"{name} must be one lowercase SHA-256 digest")
    return value


def _normalize_relative(value: str, name: str) -> str:
    value = _strict_text(value.replace("\\", "/"), name)
    normalized = str(PurePosixPath(value))
    if value.startswith("/") or normalized != value or ".." in PurePosixPath(value).parts:
        raise ValueError(f"{name} is not a canonical relative coordinate")
    return value


def _require_keys(value: dict[str, Any], expected: set[str], name: str) -> None:
    if set(value) != expected:
        raise ValueError(f"{name} has missing or unsupported fields")


def _load_manifest(path: Path = MANIFEST_PATH) -> tuple[dict[str, Any], str]:
    manifest = json.loads(path.read_bytes().decode("utf-8", "strict"))
    if not isinstance(manifest, dict):
        raise ValueError("Actor source manifest must be one JSON object")
    _require_keys(
        manifest,
        {
            "schemaVersion", "manifestId", "gameId", "steamAppId", "steamBuildId",
            "sourceFamilyId", "exactResidentRecordCount", "exactDlcRecordCount",
            "residentCandidates", "mountGraph", "formats",
        },
        "Actor source manifest",
    )
    if (
        manifest["schemaVersion"] != 1
        or manifest["manifestId"] != "grid.gta-v-enhanced.actor-source-families"
        or manifest["gameId"] != GAME_ID
        or manifest["steamAppId"] != STEAM_APP_ID
        or manifest["steamBuildId"] != "25261616"
        or manifest["sourceFamilyId"] != "rockstar.gta-v.enhanced.mounted-ped-registry"
        or manifest["exactResidentRecordCount"] != 683
        or manifest["exactDlcRecordCount"] != 435
    ):
        raise ValueError("Actor source manifest identity or exact-build counts changed")
    candidates = manifest["residentCandidates"]
    if not isinstance(candidates, list) or len(candidates) != 2:
        raise ValueError("Actor source manifest requires exactly two resident candidates")
    expected_candidates = (
        (
            "update/update.rpf!/x64/data/peds.ymt", 2, 240764,
            "af6db187add474915996977c4f91293bf8c7465dd79aea48cb01e2e9ea58a25c",
        ),
        (
            "x64a.rpf!/data/peds.ymt", 3, 240596,
            "abec57fa062f8a5e43bc7844281ae6cabc3e77175954e7c538738f7cb78d5ffe",
        ),
    )
    actual_candidates = []
    for candidate in candidates:
        if not isinstance(candidate, dict):
            raise ValueError("Resident candidate must be one object")
        _require_keys(
            candidate,
            {
                "sourceCoordinate", "sourceTier", "expectedByteLength", "expectedSha256",
                "formatId", "exactFormatVersion",
            },
            "resident candidate",
        )
        if (
            candidate["formatId"] != "rockstar.gta-v.ped-model-init-data-list-pso"
            or candidate["exactFormatVersion"] != "1"
        ):
            raise ValueError("Resident candidate format changed")
        actual_candidates.append((
            _strict_text(candidate["sourceCoordinate"], "resident source coordinate"),
            candidate["sourceTier"], candidate["expectedByteLength"],
            _lower_sha256(candidate["expectedSha256"], "resident expected digest"),
        ))
    if tuple(actual_candidates) != expected_candidates:
        raise ValueError("Resident candidate coordinates, precedence, or digests changed")

    graph = manifest["mountGraph"]
    if not isinstance(graph, dict):
        raise ValueError("Actor mount graph declaration must be one object")
    _require_keys(
        graph,
        {
            "dlcListSourceCoordinate", "dlcListExpectedByteLength", "dlcListExpectedSha256",
            "exactOccurrenceCount", "exactUniqueCasefoldedPathCount",
            "exactPlatformOccurrenceCount", "exactDlcPacksOccurrenceCount",
            "exactPhysicalDlcPacksCount", "exactPedMetadataRegistrationCount",
            "duplicateNormalizedMountPaths", "declaredPhysicalContainerExceptions",
            "pedMetadataPacks",
        },
        "Actor mount graph declaration",
    )
    if (
        graph["dlcListSourceCoordinate"] != "update/update.rpf!/common/data/dlclist.xml"
        or graph["dlcListExpectedByteLength"] != 4310
        or graph["dlcListExpectedSha256"]
        != "86a6bb9c5c8858404fd7d73b9116fb6f31a683023f058082892a9e710e1bccbc"
        or tuple(
            graph[name] for name in (
                "exactOccurrenceCount", "exactUniqueCasefoldedPathCount",
                "exactPlatformOccurrenceCount", "exactDlcPacksOccurrenceCount",
                "exactPhysicalDlcPacksCount", "exactPedMetadataRegistrationCount",
            )
        ) != (104, 103, 10, 94, 92, 25)
        or graph["duplicateNormalizedMountPaths"] != ["dlcpacks:/mp2025_01_g9ec/"]
        or graph["declaredPhysicalContainerExceptions"] != ["dlcpacks:/patchbvh00/"]
        or not isinstance(graph["pedMetadataPacks"], list)
        or len(graph["pedMetadataPacks"]) != 25
        or graph["pedMetadataPacks"] != sorted(graph["pedMetadataPacks"])
        or len(graph["pedMetadataPacks"]) != len(set(graph["pedMetadataPacks"]))
    ):
        raise ValueError("Actor mount graph exact-build closure changed")
    formats = manifest["formats"]
    if not isinstance(formats, dict) or set(formats) != {
        "dlcList", "dlcSetup", "dlcContent", "dlcPedMetadata"
    }:
        raise ValueError("Actor source formats changed")
    for name, item in formats.items():
        if not isinstance(item, dict):
            raise ValueError(f"Actor format {name} must be one object")
        _require_keys(item, {"formatId", "exactFormatVersion"}, f"Actor format {name}")
        _strict_text(item["formatId"], f"Actor format {name} ID")
        if item["exactFormatVersion"] != "1":
            raise ValueError(f"Actor format {name} version changed")
    digest = _sha256_bytes(_canonical_bytes(manifest))
    if digest != EXPECTED_MANIFEST_SHA256:
        raise ValueError("Actor source manifest does not match its approved canonical digest")
    return manifest, digest


MANIFEST, MANIFEST_SHA256 = _load_manifest()


def _load_lock(wheel: Path) -> dict[str, Any]:
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    if lock.get("schemaVersion") != 1 or lock.get("package") != "fivefury":
        raise ValueError("Unsupported FiveFury lock document")
    if importlib.metadata.version("fivefury") != lock["exactVersion"]:
        raise ValueError("Installed FiveFury version differs from the checked-in lock")
    _, digest = _sha256_file(wheel)
    if wheel.name != lock["windowsX64Wheel"] or digest != lock["windowsX64WheelSha256"]:
        raise ValueError("FiveFury wheel name or digest differs from the checked-in lock")
    return lock


def _parse_app_state_identity(text: str) -> tuple[str, str, str]:
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if len(lines) < 3 or lines[0] != '"AppState"' or lines[1] != "{":
        raise ValueError("Steam manifest requires one exact AppState root")
    names = ("appid", "buildid", "installdir")
    values: dict[str, str] = {}
    depth = 1
    awaiting = False
    closed = False
    for line in lines[2:]:
        if closed:
            raise ValueError("Steam manifest has content after AppState")
        if line == "{":
            if not awaiting:
                raise ValueError("Steam manifest has an unbound object")
            depth += 1
            awaiting = False
            continue
        if awaiting:
            raise ValueError("Steam manifest object name has no opening brace")
        if line == "}":
            depth -= 1
            closed = depth == 0
            continue
        pair = re.fullmatch(r'"([^"\r\n]+)"\s+"([^"\r\n]*)"', line)
        if pair:
            name, value = pair.groups()
            matching = next((candidate for candidate in names if candidate.lower() == name.lower()), None)
            if matching and (name != matching or depth != 1 or matching in values or not value):
                raise ValueError(f"Steam manifest has an invalid {matching} field")
            if matching:
                values[matching] = value
            continue
        object_name = re.fullmatch(r'"([^"\r\n]+)"', line)
        if not object_name:
            raise ValueError("Steam manifest has unsupported structure")
        awaiting = True
    if awaiting or not closed or depth != 0 or set(values) != set(names):
        raise ValueError("Steam manifest AppState closure is invalid")
    return values["appid"], values["buildid"], values["installdir"]


def _steam_observation(game_root: Path) -> dict[str, Any]:
    steamapps = game_root.parent.parent.resolve(strict=True)
    path = (steamapps / f"appmanifest_{STEAM_APP_ID}.acf").resolve(strict=True)
    if path.parent != steamapps:
        raise ValueError("Steam app manifest escaped steamapps")
    exact = path.read_bytes()
    app_id, build_id, install_dir = _parse_app_state_identity(exact.decode("utf-8", "strict"))
    if app_id != STEAM_APP_ID or install_dir != game_root.name:
        raise ValueError("Steam app or installation identity mismatch")
    if build_id != MANIFEST["steamBuildId"]:
        raise ValueError("Installed Steam build differs from the Actor source manifest")
    return {
        "provider": "valve.steam", "appId": app_id, "buildId": build_id,
        "installDir": install_dir,
        "manifestCoordinate": f"steamapps/appmanifest_{STEAM_APP_ID}.acf",
        "manifestByteLength": len(exact), "manifestSha256": _sha256_bytes(exact),
        "appIdFieldPath": "/AppState/appid", "buildIdFieldPath": "/AppState/buildid",
        "installDirFieldPath": "/AppState/installdir",
    }


def _resolve_game_file(game_root: Path, coordinate: str) -> Path:
    coordinate = _normalize_relative(coordinate, "game file coordinate")
    root = game_root.resolve(strict=True)
    result = root.joinpath(*PurePosixPath(coordinate).parts).resolve(strict=True)
    if root not in result.parents or not result.is_file():
        raise ValueError(f"Game file is outside the exact root or missing: {coordinate}")
    return result


def _read_entry(archive: RpfArchive, member: str) -> bytes:
    member = _normalize_relative(member, "RPF member coordinate")
    entry = archive.find_entry(member)
    if not isinstance(entry, RpfFileEntry):
        raise ValueError(f"Exact RPF member was not found: {member}")
    return archive.read_entry_bytes(entry, logical=True)


def _parse_xml(exact: bytes, expected_root: str, name: str) -> ET.Element:
    if not exact or len(exact) > MAX_XML_BYTES:
        raise ValueError(f"{name} is empty or exceeds the XML resource limit")
    text = exact.decode("utf-8-sig", "strict")
    if "<!DOCTYPE" in text.upper() or "<!ENTITY" in text.upper():
        raise ValueError(f"{name} contains a prohibited XML declaration")
    root = ET.fromstring(text)
    if root.tag != expected_root:
        raise ValueError(f"{name} requires exact root {expected_root}")
    return root


def _only_child(parent: ET.Element, tag: str, name: str) -> ET.Element:
    matches = [child for child in parent if child.tag == tag]
    if len(matches) != 1:
        raise ValueError(f"{name} requires exactly one {tag} child")
    return matches[0]


def _exact_element_text(element: ET.Element, name: str) -> str:
    if element.attrib or list(element):
        raise ValueError(f"{name} must be one scalar XML field")
    return _strict_text(element.text, name)


def _parse_dlclist_occurrences(exact: bytes) -> list[dict[str, Any]]:
    root = _parse_xml(exact, "SMandatoryPacksData", "DLC list")
    paths = _only_child(root, "Paths", "DLC list")
    result = []
    coordinate = "update/update.rpf!/common/data/dlclist.xml"
    for ordinal, item in enumerate(paths):
        if item.tag not in ("Item", "item"):
            raise ValueError("DLC list Paths contains a non-Item child")
        if list(item) or item.attrib not in ({}, {"platform": "ps5|xbsx"}):
            raise ValueError("DLC list item has unsupported structure or platform scope")
        result.append({
            "ordinal": ordinal,
            "rawMountPath": _strict_text(item.text, f"DLC list item {ordinal + 1}"),
            "fieldLocator": (
                f"rpf7-member:{coordinate}#"
                f"/SMandatoryPacksData[1]/Paths[1]/*[{ordinal + 1}]"
            ),
        })
    return result


def _parse_dlclist(exact: bytes) -> list[str]:
    return [item["rawMountPath"] for item in _parse_dlclist_occurrences(exact)]


def _normalize_mount_path(value: str) -> tuple[str, str, str]:
    platform = re.fullmatch(r"platform:/dlcPacks/([^/]+)/", value, re.IGNORECASE)
    dlcpacks = re.fullmatch(r"dlcpacks:/([^/]+)/", value, re.IGNORECASE)
    if platform:
        scheme, pack, normalized = (
            "platform", platform.group(1),
            f"platform:/dlcpacks/{platform.group(1).casefold()}/",
        )
    elif dlcpacks:
        scheme, pack, normalized = (
            "dlcpacks", dlcpacks.group(1),
            f"dlcpacks:/{dlcpacks.group(1).casefold()}/",
        )
    else:
        raise ValueError(f"Unsupported DLC mount path: {value}")
    _strict_text(pack, "DLC pack identity")
    return scheme, pack, normalized


def _parse_setup(exact: bytes) -> tuple[str, str]:
    root = _parse_xml(exact, "SSetupData", "DLC setup")
    device = _exact_element_text(_only_child(root, "deviceName", "DLC setup"), "DLC deviceName")
    dat_matches = [child for child in root if child.tag == "datFile"]
    dat_file = "content.xml" if not dat_matches else _exact_element_text(dat_matches[0], "DLC datFile")
    if len(dat_matches) > 1:
        raise ValueError("DLC setup has multiple datFile fields")
    return device, _normalize_relative(dat_file, "DLC content member")


def _parse_content(exact: bytes) -> list[tuple[int, int, str, str]]:
    root = _parse_xml(exact, "CDataFileMgr__ContentsOfDataFileXml", "DLC content")
    matches = [child for child in root if child.tag == "dataFiles"]
    if not matches:
        return []
    result = []
    for group_ordinal, data_files in enumerate(matches):
        for item_ordinal, item in enumerate(data_files):
            if item.tag != "Item":
                raise ValueError("DLC content dataFiles contains a non-Item child")
            filename = _exact_element_text(_only_child(item, "filename", "DLC data file"), "DLC filename")
            file_type = _exact_element_text(_only_child(item, "fileType", "DLC data file"), "DLC fileType")
            result.append((group_ordinal, item_ordinal, filename, file_type))
    return result


def _parse_dlc_ped_records(exact: bytes, source_coordinate: str) -> list[dict[str, Any]]:
    root = _parse_xml(exact, "CPedModelInfo__InitDataList", "DLC ped metadata")
    init_datas = _only_child(root, "InitDatas", "DLC ped metadata")
    records = []
    for ordinal, item in enumerate(init_datas):
        if item.tag != "Item":
            raise ValueError("Ped InitDatas contains a non-Item child")
        name = _exact_element_text(_only_child(item, "Name", "ped record"), "ped Name")
        pedtype = _exact_element_text(_only_child(item, "Pedtype", "ped record"), "ped Pedtype")
        base = (
            f"rpf7-member:{source_coordinate}#"
            f"/CPedModelInfo__InitDataList[1]/InitDatas[1]/Item[{ordinal + 1}]"
        )
        records.append({
            "ordinal": ordinal, "nameExact": name, "pedtypeExact": pedtype,
            "recordLocator": base, "nameFieldLocator": f"{base}/Name[1]",
            "pedtypeFieldLocator": f"{base}/Pedtype[1]",
        })
    return records


def _resident_records(exact: bytes, source_coordinate: str) -> list[dict[str, Any]]:
    metadata = read_ped_metadata(exact)
    result = []
    for ordinal, item in enumerate(metadata.init_datas):
        base = (
            f"pso:{source_coordinate}#"
            f"/CPedModelInfo__InitDataList/InitDatas/Item[{ordinal + 1}]"
        )
        result.append({
            "ordinal": ordinal, "nameHash": f"0x{item.name.uint:08X}",
            "pedtypeHash": f"0x{item.ped_type.uint:08X}", "recordLocator": base,
            "nameFieldLocator": f"{base}/Name", "pedtypeFieldLocator": f"{base}/Pedtype",
        })
    return result


def _validate_mount_closure(
    mount_paths: list[str], physical_pack_names: list[str], ped_pack_names: list[str]
) -> None:
    graph = MANIFEST["mountGraph"]
    normalized = [_normalize_mount_path(value)[2] for value in mount_paths]
    counts = Counter(normalized)
    platform = [value for value in normalized if value.startswith("platform:/")]
    dlcpacks = [value for value in normalized if value.startswith("dlcpacks:/")]
    duplicates = sorted(value for value, count in counts.items() if count > 1)
    physical = sorted(name.casefold() for name in physical_pack_names)
    declared = {value[len("dlcpacks:/"):-1] for value in dlcpacks}
    missing = sorted(f"dlcpacks:/{name}/" for name in declared - set(physical))
    if (
        len(mount_paths) != graph["exactOccurrenceCount"]
        or len(counts) != graph["exactUniqueCasefoldedPathCount"]
        or len(platform) != graph["exactPlatformOccurrenceCount"]
        or len(dlcpacks) != graph["exactDlcPacksOccurrenceCount"]
        or len(physical) != graph["exactPhysicalDlcPacksCount"]
        or len(physical) != len(set(physical))
        or duplicates != graph["duplicateNormalizedMountPaths"]
        or missing != graph["declaredPhysicalContainerExceptions"]
        or sorted(name.casefold() for name in ped_pack_names) != graph["pedMetadataPacks"]
    ):
        raise ValueError("Mounted Actor source closure differs from the exact-build manifest")


def _artifact_tuple(
    source_coordinate: str, exact: bytes, format_id: str, version: str
) -> dict[str, Any]:
    return {
        "sourceCoordinate": source_coordinate, "byteLength": len(exact),
        "sha256": _sha256_bytes(exact), "formatId": format_id,
        "formatVersion": version,
    }


def _member_output(output: Path, source_coordinate: str) -> Path:
    container, member = source_coordinate.split("!/", 1)
    return output.joinpath(
        "members", *PurePosixPath(container).parts, *PurePosixPath(member).parts
    )


def _write_frozen(output: Path, source_coordinate: str, exact: bytes) -> str:
    target = _member_output(output, source_coordinate)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("xb") as stream:
        stream.write(exact)
    return target.relative_to(output).as_posix()


def _build_snapshot(
    game_root: Path,
    lock: dict[str, Any],
    observed_at_utc: str,
    platform_observation: dict[str, Any],
    output: Path | None,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, bytes]]:
    crypto = GameCrypto.from_game(game_root, gen9=True, use_cache=False)
    container_receipts: dict[str, dict[str, Any]] = {}
    artifact_bytes: dict[str, bytes] = {}

    def open_container(coordinate: str) -> tuple[Path, RpfArchive]:
        path = _resolve_game_file(game_root, coordinate)
        if coordinate not in container_receipts:
            length, digest = _sha256_file(path)
            container_receipts[coordinate] = {
                "containerCoordinate": coordinate, "byteLength": length,
                "sha256": digest, "artifacts": [],
            }
        return path, RpfArchive.from_path(path, crypto=crypto)

    def register_artifact(
        coordinate: str, exact: bytes, role: str, format_id: str, version: str
    ) -> dict[str, Any]:
        if coordinate in artifact_bytes and artifact_bytes[coordinate] != exact:
            raise ValueError("One source coordinate produced unequal artifact bytes")
        artifact_bytes[coordinate] = exact
        container_coordinate = coordinate.split("!/", 1)[0]
        item = _artifact_tuple(coordinate, exact, format_id, version)
        item["role"] = role
        item["memberPath"] = coordinate.split("!/", 1)[1]
        if output is not None:
            item["frozenRelativePath"] = _write_frozen(output, coordinate, exact)
        else:
            item["frozenRelativePath"] = (
                Path("members") / PurePosixPath(container_coordinate)
                / PurePosixPath(item["memberPath"])
            ).as_posix()
        container_receipts[container_coordinate]["artifacts"].append(item)
        return _artifact_tuple(coordinate, exact, format_id, version)

    resident_candidates = []
    resident_raw: dict[str, bytes] = {}
    for declaration in MANIFEST["residentCandidates"]:
        coordinate = declaration["sourceCoordinate"]
        container_coordinate, member = coordinate.split("!/", 1)
        _, archive = open_container(container_coordinate)
        try:
            exact = _read_entry(archive, member)
        finally:
            archive.close()
        if (
            len(exact) != declaration["expectedByteLength"]
            or _sha256_bytes(exact) != declaration["expectedSha256"]
        ):
            raise ValueError(f"Resident ped candidate changed: {coordinate}")
        artifact = register_artifact(
            coordinate, exact, "resident-ped-registry-candidate",
            declaration["formatId"], declaration["exactFormatVersion"],
        )
        artifact["sourceTier"] = declaration["sourceTier"]
        resident_candidates.append(artifact)
        resident_raw[coordinate] = exact
    winner_tier = min(item["sourceTier"] for item in resident_candidates)
    winners = [item for item in resident_candidates if item["sourceTier"] == winner_tier]
    if len(winners) != 1 or winners[0]["sourceCoordinate"] != (
        "update/update.rpf!/x64/data/peds.ymt"
    ):
        raise ValueError("Resident ped precedence has no unique approved winner")
    effective = winners[0]
    resident_records = _resident_records(
        resident_raw[effective["sourceCoordinate"]], effective["sourceCoordinate"]
    )
    if len(resident_records) != MANIFEST["exactResidentRecordCount"]:
        raise ValueError("Resident ped record cardinality changed")

    update_coordinate = "update/update.rpf"
    _, update_archive = open_container(update_coordinate)
    try:
        dlclist_exact = _read_entry(update_archive, "common/data/dlclist.xml")
    finally:
        update_archive.close()
    graph = MANIFEST["mountGraph"]
    if (
        len(dlclist_exact) != graph["dlcListExpectedByteLength"]
        or _sha256_bytes(dlclist_exact) != graph["dlcListExpectedSha256"]
    ):
        raise ValueError("DLC list bytes changed for the exact Steam build")
    dlclist_artifact = register_artifact(
        graph["dlcListSourceCoordinate"], dlclist_exact, "dlc-mount-list",
        MANIFEST["formats"]["dlcList"]["formatId"], "1",
    )
    dlclist_occurrences = _parse_dlclist_occurrences(dlclist_exact)
    mount_paths = [item["rawMountPath"] for item in dlclist_occurrences]
    occurrences_by_pack: dict[str, list[int]] = {}
    raw_pack_by_key: dict[str, str] = {}
    mount_ledger = []
    for occurrence in dlclist_occurrences:
        ordinal = occurrence["ordinal"]
        raw = occurrence["rawMountPath"]
        scheme, pack, normalized = _normalize_mount_path(raw)
        if scheme == "dlcpacks":
            key = pack.casefold()
            occurrences_by_pack.setdefault(key, []).append(ordinal)
            raw_pack_by_key.setdefault(key, pack)
        mount_ledger.append({
            "ordinal": ordinal, "rawMountPath": raw,
            "fieldLocator": occurrence["fieldLocator"],
            "normalizedMountPath": normalized, "mountScheme": scheme,
            "packNameExact": pack,
            "status": "platform-mount-outside-physical-dlc-boundary" if scheme == "platform" else "pending",
        })

    physical_root = game_root / "update" / "x64" / "dlcpacks"
    physical_directories = sorted(
        (item for item in physical_root.iterdir() if item.is_dir()),
        key=lambda item: item.name.casefold(),
    )
    physical_by_key: dict[str, Path] = {}
    for directory in physical_directories:
        key = directory.name.casefold()
        if key in physical_by_key:
            raise ValueError("Physical DLC pack names collide case-insensitively")
        physical_by_key[key] = directory
    dlc_index_packs = []
    ped_pack_names = []
    dlc_record_count = 0
    ledger_by_key = {
        item["packNameExact"].casefold(): [] for item in mount_ledger
        if item["mountScheme"] == "dlcpacks"
    }
    for item in mount_ledger:
        if item["mountScheme"] == "dlcpacks":
            ledger_by_key[item["packNameExact"].casefold()].append(item)

    for key in sorted(occurrences_by_pack):
        directory = physical_by_key.get(key)
        ledger_items = ledger_by_key[key]
        if directory is None:
            for item in ledger_items:
                item["status"] = "declared-container-absent"
            continue
        container_coordinate = (
            f"update/x64/dlcpacks/{directory.name}/dlc.rpf"
        )
        _, archive = open_container(container_coordinate)
        try:
            setup_exact = _read_entry(archive, "setup2.xml")
            device_name, content_member = _parse_setup(setup_exact)
            content_exact = _read_entry(archive, content_member)
            declarations = [
                value for value in _parse_content(content_exact)
                if value[3] == "PED_METADATA_FILE"
            ]
            if len(declarations) > 1:
                raise ValueError(f"DLC pack has multiple PED_METADATA_FILE declarations: {key}")
            setup_coordinate = f"{container_coordinate}!/setup2.xml"
            content_coordinate = f"{container_coordinate}!/{content_member}"
            setup_artifact = register_artifact(
                setup_coordinate, setup_exact, "dlc-setup",
                MANIFEST["formats"]["dlcSetup"]["formatId"], "1",
            )
            content_artifact = register_artifact(
                content_coordinate, content_exact, "dlc-content-registry",
                MANIFEST["formats"]["dlcContent"]["formatId"], "1",
            )
            if not declarations:
                for item in ledger_items:
                    item["status"] = "resolved-no-ped-metadata-declaration"
                    item["physicalContainerCoordinate"] = container_coordinate
                continue
            data_files_ordinal, declaration_ordinal, filename, _ = declarations[0]
            prefix = f"{device_name}:/"
            if not filename.casefold().startswith(prefix.casefold()):
                raise ValueError(f"PED metadata filename does not use setup device: {filename}")
            peds_member = _normalize_relative(
                filename[len(prefix):], "DLC peds metadata member"
            )
            if peds_member.casefold() != "common/data/peds.meta":
                raise ValueError(f"Unexpected PED metadata member: {peds_member}")
            peds_exact = _read_entry(archive, peds_member)
            peds_coordinate = f"{container_coordinate}!/{peds_member}"
            peds_artifact = register_artifact(
                peds_coordinate, peds_exact, "dlc-ped-registry",
                MANIFEST["formats"]["dlcPedMetadata"]["formatId"], "1",
            )
            records = _parse_dlc_ped_records(peds_exact, peds_coordinate)
            dlc_record_count += len(records)
            pack_name = raw_pack_by_key[key]
            ped_pack_names.append(pack_name.casefold())
            declaration_locator = (
                f"rpf7-member:{content_coordinate}#"
                f"/CDataFileMgr__ContentsOfDataFileXml[1]/dataFiles[{data_files_ordinal + 1}]"
                f"/Item[{declaration_ordinal + 1}]"
            )
            dlc_index_packs.append({
                "packNameExact": pack_name,
                "dlclistOccurrenceOrdinals": occurrences_by_pack[key],
                "dlclistOccurrences": [
                    {
                        "ordinal": item["ordinal"],
                        "rawMountPath": item["rawMountPath"],
                        "fieldLocator": item["fieldLocator"],
                    }
                    for item in ledger_items
                ],
                "normalizedMountPath": f"dlcpacks:/{key}/",
                "physicalContainerCoordinate": container_coordinate,
                "setupArtifact": setup_artifact, "contentArtifact": content_artifact,
                "pedMetadataDeclarationLocator": declaration_locator,
                "pedsArtifact": peds_artifact, "records": records,
            })
            for item in ledger_items:
                item["status"] = "resolved-ped-metadata"
                item["physicalContainerCoordinate"] = container_coordinate
                item["setupCoordinate"] = setup_coordinate
                item["contentCoordinate"] = content_coordinate
                item["pedsCoordinate"] = peds_coordinate
                item["pedMetadataDeclarationLocator"] = declaration_locator
        finally:
            archive.close()
    _validate_mount_closure(
        mount_paths, [item.name for item in physical_directories], ped_pack_names
    )
    if dlc_record_count != MANIFEST["exactDlcRecordCount"]:
        raise ValueError("DLC ped record cardinality changed")

    for receipt in container_receipts.values():
        receipt["artifacts"].sort(key=lambda value: value["sourceCoordinate"])
    receipt = {
        "schemaVersion": SCHEMA_VERSION, "gameId": GAME_ID,
        "gameVersionNamespace": "valve.steam.app.3240220.build-id",
        "gameVersion": platform_observation["buildId"],
        "platformObservation": platform_observation, "observedAtUtc": observed_at_utc,
        "acquisitionTool": {
            "toolId": "fivefury.rpf-read+ped-metadata", "exactVersion": lock["exactVersion"],
            "artifactSha256": lock["windowsX64WheelSha256"],
            "sourceRevision": lock["sourceRevision"],
            "methodId": "grid.gta-v-enhanced.actor-mounted-ped-acquisition",
            "methodVersion": "1", "receiptSchemaVersion": SCHEMA_VERSION,
        },
        "sourceFamilyManifest": {
            "manifestId": MANIFEST["manifestId"], "schemaVersion": MANIFEST["schemaVersion"],
            "documentSha256": MANIFEST_SHA256,
        },
        "residentPrecedence": {
            "methodId": "grid.gta-v.asset-source-tier", "methodVersion": "1",
            "winningSourceTier": winner_tier,
            "effectiveSourceCoordinate": effective["sourceCoordinate"],
        },
        "dlcMountGraph": {
            "dlcListArtifact": dlclist_artifact,
            "occurrences": mount_ledger,
            "occurrenceCount": len(mount_paths),
            "uniqueCasefoldedPathCount": len({_normalize_mount_path(value)[2] for value in mount_paths}),
            "physicalDlcPacksCount": len(physical_directories),
            "pedMetadataRegistrationCount": len(dlc_index_packs),
        },
        "containers": sorted(container_receipts.values(), key=lambda value: value["containerCoordinate"]),
    }
    index = {
        "schemaId": "grid.gta-v.actor-corpus-index", "schemaVersion": INDEX_SCHEMA_VERSION,
        "decoder": {
            "methodId": "fivefury.ymt.ped-metadata", "exactVersion": lock["exactVersion"],
            "artifactSha256": lock["windowsX64WheelSha256"],
        },
        "resident": {
            "effectiveArtifact": {
                key: effective[key] for key in (
                    "sourceCoordinate", "byteLength", "sha256", "formatId", "formatVersion",
                    "sourceTier",
                )
            },
            "candidateArtifacts": sorted(resident_candidates, key=lambda value: value["sourceCoordinate"]),
            "records": resident_records,
        },
        "dlcListArtifact": dlclist_artifact,
        "dlcPacks": sorted(dlc_index_packs, key=lambda value: value["normalizedMountPath"]),
    }
    index_document = _canonical_bytes(index) + b"\n"
    receipt["actorCorpusIndex"] = {
        "fileName": INDEX_NAME,
        "schemaId": index["schemaId"],
        "schemaVersion": index["schemaVersion"],
        "decoderMethodId": index["decoder"]["methodId"],
        "decoderExactVersion": index["decoder"]["exactVersion"],
        "decoderArtifactSha256": index["decoder"]["artifactSha256"],
        "documentByteLength": len(index_document),
        "documentSha256": _sha256_bytes(index_document),
    }
    return receipt, index, artifact_bytes


def _validate_observed(value: str) -> str:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.utcoffset() is None or parsed.utcoffset().total_seconds() != 0 or not value.endswith("Z"):
        raise ValueError("observed-at-utc must be one exact UTC timestamp ending in Z")
    return value


def _validate_index_binding(
    binding: Any, index_document: bytes, lock: dict[str, Any]
) -> None:
    if not isinstance(binding, dict):
        raise ValueError("Actor corpus index binding is missing")
    expected = {
        "fileName": INDEX_NAME,
        "schemaId": "grid.gta-v.actor-corpus-index",
        "schemaVersion": 1,
        "decoderMethodId": "fivefury.ymt.ped-metadata",
        "decoderExactVersion": lock["exactVersion"],
        "decoderArtifactSha256": lock["windowsX64WheelSha256"],
        "documentByteLength": len(index_document),
        "documentSha256": _sha256_bytes(index_document),
    }
    if binding != expected:
        raise ValueError("Actor corpus index bytes or decoder binding mismatch")
    parsed = json.loads(index_document.decode("utf-8", "strict"))
    if index_document != _canonical_bytes(parsed) + b"\n":
        raise ValueError("Actor corpus index is not exact canonical JSON plus LF")


def acquire(args: argparse.Namespace, lock: dict[str, Any]) -> None:
    output = Path(args.output).resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Actor acquisition output must be absent or empty")
    output.mkdir(parents=True, exist_ok=True)
    game_root = Path(args.game_root).resolve(strict=True)
    observed = _validate_observed(args.observed_at_utc)
    receipt, index, _ = _build_snapshot(
        game_root, lock, observed, _steam_observation(game_root), output
    )
    receipt["receiptDocumentSha256"] = _sha256_bytes(_canonical_bytes(receipt))
    (output / RECEIPT_NAME).write_bytes(_canonical_bytes(receipt) + b"\n")
    (output / INDEX_NAME).write_bytes(_canonical_bytes(index) + b"\n")


def verify(args: argparse.Namespace, lock: dict[str, Any]) -> None:
    receipt_path = Path(args.receipt).resolve(strict=True)
    root = receipt_path.parent
    receipt = json.loads(receipt_path.read_bytes().decode("utf-8", "strict"))
    digest = receipt.pop("receiptDocumentSha256", None)
    if digest != _sha256_bytes(_canonical_bytes(receipt)):
        raise ValueError("Actor acquisition receipt document digest mismatch")
    if receipt.get("schemaVersion") != 1 or receipt.get("gameId") != GAME_ID:
        raise ValueError("Actor acquisition receipt schema or GameId mismatch")
    if receipt.get("sourceFamilyManifest") != {
        "manifestId": MANIFEST["manifestId"], "schemaVersion": 1,
        "documentSha256": MANIFEST_SHA256,
    }:
        raise ValueError("Actor acquisition receipt manifest binding mismatch")
    expected_tool = {
        "toolId": "fivefury.rpf-read+ped-metadata", "exactVersion": lock["exactVersion"],
        "artifactSha256": lock["windowsX64WheelSha256"],
        "sourceRevision": lock["sourceRevision"],
        "methodId": "grid.gta-v-enhanced.actor-mounted-ped-acquisition",
        "methodVersion": "1", "receiptSchemaVersion": 1,
    }
    if receipt.get("acquisitionTool") != expected_tool:
        raise ValueError("Actor acquisition tool coordinate mismatch")
    _validate_observed(receipt.get("observedAtUtc"))
    game_root = Path(args.game_root).resolve(strict=True)
    actual_platform = _steam_observation(game_root)
    expected_platform = receipt.get("platformObservation")
    stable = (
        "provider", "appId", "buildId", "installDir", "manifestCoordinate",
        "appIdFieldPath", "buildIdFieldPath", "installDirFieldPath",
    )
    if not isinstance(expected_platform, dict) or any(
        expected_platform.get(name) != actual_platform[name] for name in stable
    ):
        raise ValueError("Actor receipt Steam app/build/install observation mismatch")
    _lower_sha256(expected_platform.get("manifestSha256"), "historical Steam manifest digest")
    if not isinstance(expected_platform.get("manifestByteLength"), int) or expected_platform["manifestByteLength"] <= 0:
        raise ValueError("Historical Steam manifest length is invalid")
    index_path = root / INDEX_NAME
    index_document = index_path.read_bytes()
    _validate_index_binding(receipt.get("actorCorpusIndex"), index_document, lock)
    actual_receipt, actual_index, artifact_bytes = _build_snapshot(
        game_root, lock, receipt["observedAtUtc"], expected_platform, None
    )
    if _canonical_bytes(actual_receipt) != _canonical_bytes(receipt):
        raise ValueError("Actor acquisition receipt does not replay from exact source bytes")
    if index_document != _canonical_bytes(actual_index) + b"\n":
        raise ValueError("Actor corpus index differs from deterministic replay")
    for coordinate, exact in artifact_bytes.items():
        frozen = _member_output(root, coordinate)
        if not frozen.is_file() or frozen.read_bytes() != exact:
            raise ValueError(f"Frozen Actor artifact differs from source: {coordinate}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("acquire", "verify"))
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--fivefury-wheel", required=True)
    parser.add_argument("--output")
    parser.add_argument("--receipt")
    parser.add_argument("--observed-at-utc")
    args = parser.parse_args()
    if args.command == "acquire" and (not args.output or not args.observed_at_utc):
        parser.error("acquire requires --output and --observed-at-utc")
    if args.command == "verify" and not args.receipt:
        parser.error("verify requires --receipt")
    return args


def main() -> int:
    args = parse_args()
    lock = _load_lock(Path(args.fivefury_wheel).resolve(strict=True))
    if args.command == "acquire":
        acquire(args, lock)
    else:
        verify(args, lock)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
