"""Read-only mounted MLO/interior acquisition for GTA V Enhanced.

The v1 boundary follows the exact effective DLC mount list into declared
levels/gta5 RPF_FILE resources, freezes YTYP/YMAP/YMF bytes, and emits a
receipt-bound semantic index.  It does not create canonical GRID records.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath
from typing import Any

from fivefury.crypto import GameCrypto
from fivefury.rpf import RpfArchive, RpfFileEntry
from fivefury.ymap import MloInstanceDef, read_ymap
from fivefury.ymf import iter_ymf_relationships, read_ymf
from fivefury.ytyp import MloArchetypeDef, read_ytyp

import gta_v_enhanced_actor_acquire as actor


SCHEMA_VERSION = 1
INDEX_SCHEMA_VERSION = 1
GAME_ID = actor.GAME_ID
STEAM_APP_ID = actor.STEAM_APP_ID
MANIFEST_PATH = Path(__file__).with_name(
    "gta_v_enhanced_spatial_source_families.v1.json"
)
RECEIPT_NAME = "gta-v-enhanced-spatial-acquisition-receipt.v1.json"
INDEX_NAME = "spatial-corpus-index.v1.json"
MAX_ARTIFACT_BYTES = 64 * 1024 * 1024


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8", "strict")


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _load_manifest() -> tuple[dict[str, Any], str]:
    exact = MANIFEST_PATH.read_bytes()
    value = json.loads(exact.decode("utf-8", "strict"))
    if not isinstance(value, dict) or set(value) != {
        "schemaVersion", "manifestId", "gameId", "steamAppId", "steamBuildId",
        "sourceFamilyId", "mountScope", "formats", "exactBuildClosure",
        "unsupportedFamilies",
    }:
        raise ValueError("Spatial manifest has missing or unsupported fields")
    if (
        value["schemaVersion"] != 1
        or value["manifestId"] != "grid.gta-v-enhanced.spatial-source-families"
        or value["gameId"] != GAME_ID
        or value["steamAppId"] != STEAM_APP_ID
        or value["steamBuildId"] != actor.MANIFEST["steamBuildId"]
        or value["sourceFamilyId"]
        != "rockstar.gta-v.enhanced.mounted-mlo-interior-graph"
        or value["mountScope"]
        != "effective-dlclist-dlcpacks-levels-gta5-rpf-file-declarations"
    ):
        raise ValueError("Spatial manifest identity changed")
    expected_formats = {
        "dlcList": ("rockstar.gta-v.dlc-list-xml", "1"),
        "dlcSetup": ("rockstar.gta-v.dlc-setup-xml", "1"),
        "dlcContent": ("rockstar.gta-v.dlc-content-xml", "1"),
        "nestedRpf": ("rockstar.rpf7", "7"),
        "ytyp": ("rockstar.gta-v.ytyp-pso", "1"),
        "ymap": ("rockstar.gta-v.ymap-pso", "1"),
        "ymf": ("rockstar.gta-v.ymf-pso", "1"),
    }
    if not isinstance(value["formats"], dict) or set(value["formats"]) != set(expected_formats):
        raise ValueError("Spatial manifest format set changed")
    for key, expected in expected_formats.items():
        item = value["formats"][key]
        if item != {"formatId": expected[0], "exactFormatVersion": expected[1]}:
            raise ValueError(f"Spatial manifest format changed: {key}")
    expected_counts = {
        "spatialPackCount", "declaredSpatialRpfCount", "nestedRpfResolvedCount",
        "ytypArtifactCount", "ymapArtifactCount", "ymfArtifactCount",
        "discoveredYmfArtifactCount", "equivalentDuplicateArtifactCount",
        "mloArchetypeCount", "mloRoomCount", "mloPortalCount",
        "mloInstanceCount", "ymfRelationshipCount", "unsupportedOutcomeCount",
    }
    closure = value["exactBuildClosure"]
    if (
        not isinstance(closure, dict)
        or set(closure) != expected_counts
        or any(not isinstance(item, int) or item < 0 for item in closure.values())
    ):
        raise ValueError("Spatial exact-build closure is invalid")
    return value, _sha256(_canonical_bytes(value))


MANIFEST, MANIFEST_SHA256 = _load_manifest()


def _uint(value: Any) -> int:
    return int(value) & 0xFFFFFFFF


def _hash_text(value: Any) -> str:
    return f"0x{_uint(value):08X}"


def _vector(value: Any) -> list[float]:
    return [float(value.x), float(value.y), float(value.z)]


def _quaternion(value: Any) -> list[float]:
    return [float(value.x), float(value.y), float(value.z), float(value.w)]


def _nested_coordinate(container: str, nested_member: str, member: str) -> str:
    return f"{container}!/{nested_member}!/{member}"


def _frozen_path(output: Path, coordinate: str) -> Path:
    parts: list[str] = []
    sections = coordinate.split("!/")
    for section_ordinal, section in enumerate(sections):
        section_parts = list(PurePosixPath(section).parts)
        if section_ordinal < len(sections) - 1:
            section_parts[-1] += ".container"
        parts.extend(section_parts)
    return output.joinpath("members", *parts)


def _write_frozen(output: Path, coordinate: str, exact: bytes) -> str:
    target = _frozen_path(output, coordinate)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("xb") as stream:
        stream.write(exact)
    return target.relative_to(output).as_posix()


def _artifact(coordinate: str, exact: bytes, format_key: str) -> dict[str, Any]:
    declaration = MANIFEST["formats"][format_key]
    return {
        "sourceCoordinate": coordinate,
        "byteLength": len(exact),
        "sha256": _sha256(exact),
        "formatId": declaration["formatId"],
        "formatVersion": declaration["exactFormatVersion"],
    }


def _parse_ytyp(exact: bytes, coordinate: str) -> list[dict[str, Any]]:
    parsed = read_ytyp(exact)
    result = []
    for source_ordinal, item in enumerate(parsed.archetypes):
        if not isinstance(item, MloArchetypeDef):
            continue
        archetype_ordinal = len(result)
        base = f"rpf7-member:{coordinate}#/archetypes[{source_ordinal + 1}]"
        rooms = []
        for room_ordinal, room in enumerate(item.rooms):
            rooms.append({
                "ordinal": room_ordinal,
                "nameExact": room.name,
                "fieldLocator": f"{base}/rooms[{room_ordinal + 1}]",
                "bounds": {
                    "min": _vector(room.bb_min),
                    "max": _vector(room.bb_max),
                },
                "flags": int(room.flags),
                "floorId": int(room.floor_id),
            })
        portals = []
        for portal_ordinal, portal in enumerate(item.portals):
            portals.append({
                "ordinal": portal_ordinal,
                "roomFromOrdinal": int(portal.room_from),
                "roomToOrdinal": int(portal.room_to),
                "fieldLocator": f"{base}/portals[{portal_ordinal + 1}]",
            })
        result.append({
            "ordinal": archetype_ordinal,
            "nameHash": _hash_text(item.name),
            "assetNameHash": _hash_text(item.asset_name),
            "nativeType": "CMloArchetypeDef",
            "fieldLocator": base,
            "rooms": rooms,
            "portals": portals,
            "entitySets": [],
        })
    return result


def _parse_ymap(exact: bytes, coordinate: str) -> tuple[str, list[dict[str, Any]]]:
    parsed = read_ymap(exact)
    instances = []
    for source_ordinal, item in enumerate(parsed.entities):
        if not isinstance(item, MloInstanceDef):
            continue
        ordinal = len(instances)
        instances.append({
            "ordinal": ordinal,
            "guid": _hash_text(item.guid),
            "archetypeNameHash": _hash_text(item.archetype_name),
            "fieldLocator": f"rpf7-member:{coordinate}#/entities[{source_ordinal + 1}]",
            "position": _vector(item.position),
            "rotation": _quaternion(item.rotation),
            "groupId": int(item.group_id),
            "floorId": int(item.floor_id),
        })
    return _hash_text(parsed.name), instances


def _parse_ymf(exact: bytes, coordinate: str) -> list[dict[str, Any]]:
    parsed = read_ymf(exact)
    result = []
    for ordinal, relation in enumerate(iter_ymf_relationships(parsed)):
        result.append({
            "ordinal": ordinal,
            "relationshipType": relation.kind.name,
            "sourceHash": _hash_text(relation.source),
            "targetHash": _hash_text(relation.target),
            "flags": int(relation.flags),
            "sourceIndex": int(relation.source_index),
            "targetIndex": int(relation.target_index),
            "fieldLocator": f"rpf7-member:{coordinate}#/relationships[{ordinal + 1}]",
        })
    return result


def _spatial_rpf_member(device_name: str, filename: str) -> str | None:
    prefix = f"{device_name}:/"
    if not filename.casefold().startswith(prefix.casefold()):
        return None
    member = filename[len(prefix):].replace("%PLATFORM%", "x64")
    member = actor._normalize_relative(member, "spatial RPF member")
    lowered = member.casefold()
    if not lowered.endswith(".rpf") or "/levels/gta5/" not in f"/{lowered}":
        return None
    return member


def _select_effective_semantic(
    mounted: list[tuple[str, dict[str, Any], int, str]],
) -> tuple[list[tuple[str, dict[str, Any], int, str]], list[dict[str, Any]]]:
    effective = []
    duplicates = []
    by_digest: dict[str, list[tuple[str, dict[str, Any], int, str]]] = {}
    for value in mounted:
        by_digest.setdefault(value[1]["artifact"]["sha256"], []).append(value)
    for digest in sorted(by_digest):
        candidates = by_digest[digest]
        formats = {item[1]["artifact"]["formatId"] for item in candidates}
        if len(formats) != 1:
            raise ValueError("Digest-identical spatial artifacts declare unequal formats")
        highest = max(item[2] for item in candidates)
        winners = [item for item in candidates if item[2] == highest]
        if len(winners) != 1:
            raise ValueError("Digest-identical spatial artifacts have ambiguous mount precedence")
        winner = winners[0]
        effective.append(winner)
        for loser in candidates:
            if loser is winner:
                continue
            duplicates.append({
                "sourceCoordinate": loser[1]["artifact"]["sourceCoordinate"],
                "formatId": loser[1]["artifact"]["formatId"],
                "reasonCode": "equivalent-content-shadowed-by-mount-precedence",
                "detailCode": winner[1]["artifact"]["sourceCoordinate"],
            })
    return effective, duplicates


def _build_snapshot(
    game_root: Path,
    lock: dict[str, Any],
    observed_at_utc: str,
    platform_observation: dict[str, Any],
    output: Path | None,
) -> tuple[dict[str, Any], dict[str, Any]]:
    crypto = GameCrypto.from_game(game_root, gen9=True, use_cache=False)
    containers: dict[str, dict[str, Any]] = {}
    artifacts: dict[str, dict[str, Any]] = {}

    def open_outer(coordinate: str) -> RpfArchive:
        path = actor._resolve_game_file(game_root, coordinate)
        if coordinate not in containers:
            length, digest = actor._sha256_file(path)
            containers[coordinate] = {
                "containerCoordinate": coordinate,
                "byteLength": length,
                "sha256": digest,
                "artifacts": [],
            }
        return RpfArchive.from_path(path, crypto=crypto)

    def register(
        outer: str, coordinate: str, exact: bytes, format_key: str, role: str,
        *, freeze: bool = True,
    ) -> dict[str, Any]:
        item = _artifact(coordinate, exact, format_key)
        prior = artifacts.get(coordinate)
        if prior is not None and prior != item:
            raise ValueError("One spatial coordinate produced unequal bytes")
        if prior is None:
            stored = dict(item)
            stored["role"] = role
            if freeze:
                stored["frozenRelativePath"] = (
                    _write_frozen(output, coordinate, exact)
                    if output is not None else _frozen_path(Path("."), coordinate).as_posix()
                )
            containers[outer]["artifacts"].append(stored)
            artifacts[coordinate] = item
        return item

    update_outer = "update/update.rpf"
    update = open_outer(update_outer)
    try:
        dlclist_exact = actor._read_entry(update, "common/data/dlclist.xml")
    finally:
        update.close()
    graph = actor.MANIFEST["mountGraph"]
    if (
        len(dlclist_exact) != graph["dlcListExpectedByteLength"]
        or _sha256(dlclist_exact) != graph["dlcListExpectedSha256"]
    ):
        raise ValueError("Spatial acquisition DLC list differs from approved build")
    dlclist_coordinate = graph["dlcListSourceCoordinate"]
    dlclist_artifact = register(
        update_outer, dlclist_coordinate, dlclist_exact, "dlcList", "mount-list"
    )
    occurrences = actor._parse_dlclist_occurrences(dlclist_exact)
    dlcpacks: dict[str, tuple[str, list[dict[str, Any]]]] = {}
    for occurrence in occurrences:
        scheme, pack, _ = actor._normalize_mount_path(occurrence["rawMountPath"])
        if scheme == "dlcpacks":
            key = pack.casefold()
            dlcpacks.setdefault(key, (pack, []))[1].append(occurrence)

    packs = []
    unsupported = []
    declared_count = 0
    resolved_count = 0
    physical_root = game_root / "update" / "x64" / "dlcpacks"
    physical = {item.name.casefold(): item for item in physical_root.iterdir() if item.is_dir()}
    for key in sorted(dlcpacks):
        pack_name, pack_occurrences = dlcpacks[key]
        directory = physical.get(key)
        if directory is None:
            unsupported.append({
                "packIdentity": pack_name,
                "sourceCoordinate": dlclist_coordinate,
                "reasonCode": "declared-container-absent",
            })
            continue
        outer_coordinate = f"update/x64/dlcpacks/{directory.name}/dlc.rpf"
        outer = open_outer(outer_coordinate)
        try:
            setup_exact = actor._read_entry(outer, "setup2.xml")
            device, content_member = actor._parse_setup(setup_exact)
            content_exact = actor._read_entry(outer, content_member)
            declarations = actor._parse_content(content_exact)
            setup_coordinate = f"{outer_coordinate}!/setup2.xml"
            content_coordinate = f"{outer_coordinate}!/{content_member}"
            setup_artifact = register(
                outer_coordinate, setup_coordinate, setup_exact, "dlcSetup", "mount-setup"
            )
            content_artifact = register(
                outer_coordinate, content_coordinate, content_exact, "dlcContent", "mount-content"
            )
            rpf_declarations = []
            seen_members: set[str] = set()
            for data_files_ordinal, declaration_ordinal, filename, file_type in declarations:
                if file_type != "RPF_FILE":
                    continue
                member = _spatial_rpf_member(device, filename)
                if member is None:
                    continue
                declared_count += 1
                declaration_locator = (
                    f"rpf7-member:{content_coordinate}#/CDataFileMgr__ContentsOfDataFileXml[1]"
                    f"/dataFiles[{data_files_ordinal + 1}]/Item[{declaration_ordinal + 1}]"
                )
                if member.casefold() in seen_members:
                    unsupported.append({
                        "packIdentity": pack_name,
                        "sourceCoordinate": content_coordinate,
                        "fieldLocator": declaration_locator,
                        "reasonCode": "duplicate-spatial-rpf-declaration",
                    })
                    continue
                seen_members.add(member.casefold())
                entry = outer.find_entry(member)
                if not isinstance(entry, RpfFileEntry):
                    unsupported.append({
                        "packIdentity": pack_name,
                        "sourceCoordinate": f"{outer_coordinate}!/{member}",
                        "fieldLocator": declaration_locator,
                        "reasonCode": "declared-spatial-rpf-missing",
                    })
                    continue
                nested_exact = outer.read_entry_bytes(entry, logical=True)
                nested_coordinate = f"{outer_coordinate}!/{member}"
                # The nested archive digest closes the declared mount edge, but the
                # archive blob is not copied into the frozen corpus.  Exact semantic
                # members bind directly to the immutable outer-container receipt;
                # duplicating every nested RPF would retain gigabytes of unrelated
                # Rockstar bytes without strengthening that evidence chain.
                nested_artifact = _artifact(
                    nested_coordinate, nested_exact, "nestedRpf"
                )
                try:
                    nested = RpfArchive.from_bytes(
                        nested_exact, name=Path(member).name, crypto=crypto
                    )
                except Exception as exc:
                    unsupported.append({
                        "packIdentity": pack_name,
                        "sourceCoordinate": nested_coordinate,
                        "fieldLocator": declaration_locator,
                        "reasonCode": "nested-rpf-unsupported",
                        "exceptionType": type(exc).__name__,
                    })
                    continue
                ytyps = []
                ymaps = []
                ymfs = []
                try:
                    for child in nested.iter_entries():
                        suffix = Path(child.name).suffix.casefold()
                        if suffix not in (".ytyp", ".ymap", ".ymf"):
                            continue
                        exact = nested.read_entry_bytes(child, logical=True)
                        if not exact or len(exact) > MAX_ARTIFACT_BYTES:
                            unsupported.append({
                                "packIdentity": pack_name,
                                "sourceCoordinate": _nested_coordinate(
                                    outer_coordinate, member, child.path
                                ),
                                "reasonCode": "spatial-artifact-size-invalid",
                            })
                            continue
                        coordinate = _nested_coordinate(outer_coordinate, member, child.path)
                        try:
                            if suffix == ".ytyp":
                                decoded = _parse_ytyp(exact, coordinate)
                                if decoded:
                                    art = register(
                                        outer_coordinate, coordinate, exact, "ytyp", "mlo-archetypes"
                                    )
                                    ytyps.append({"artifact": art, "archetypes": decoded})
                            elif suffix == ".ymap":
                                name_hash, decoded = _parse_ymap(exact, coordinate)
                                if decoded:
                                    art = register(
                                        outer_coordinate, coordinate, exact, "ymap", "mlo-instances"
                                    )
                                    ymaps.append({
                                        "artifact": art, "mapNameHash": name_hash,
                                        "instances": decoded,
                                    })
                            else:
                                decoded = _parse_ymf(exact, coordinate)
                                art = register(
                                    outer_coordinate, coordinate, exact, "ymf", "spatial-manifest"
                                )
                                ymfs.append({"artifact": art, "relationships": decoded})
                        except Exception as exc:
                            unsupported.append({
                                "packIdentity": pack_name,
                                "sourceCoordinate": coordinate,
                                "reasonCode": f"{suffix[1:]}-decoder-unsupported",
                                "exceptionType": type(exc).__name__,
                            })
                finally:
                    nested.close()
                resolved_count += 1
                rpf_declarations.append({
                    "memberPath": member,
                    "declarationLocator": declaration_locator,
                    "artifact": nested_artifact,
                    "ytyps": sorted(ytyps, key=lambda item: item["artifact"]["sourceCoordinate"]),
                    "ymaps": sorted(ymaps, key=lambda item: item["artifact"]["sourceCoordinate"]),
                    "ymfs": sorted(ymfs, key=lambda item: item["artifact"]["sourceCoordinate"]),
                })
            if rpf_declarations:
                packs.append({
                    "packIdentity": pack_name,
                    "normalizedMountPath": f"dlcpacks:/{key}/",
                    "dlclistOccurrences": pack_occurrences,
                    "setupArtifact": setup_artifact,
                    "contentArtifact": content_artifact,
                    "spatialRpfDeclarations": sorted(
                        rpf_declarations, key=lambda item: item["memberPath"].casefold()
                    ),
                })
        finally:
            outer.close()

    flattened = [rpf for pack in packs for rpf in pack["spatialRpfDeclarations"]]
    mounted_semantic = []
    for pack in packs:
        precedence = max(item["ordinal"] for item in pack["dlclistOccurrences"])
        for rpf in pack["spatialRpfDeclarations"]:
            for family in ("ytyps", "ymaps", "ymfs"):
                for item in rpf[family]:
                    mounted_semantic.append((family, item, precedence, pack["packIdentity"]))
    effective_semantic, equivalent_duplicates = _select_effective_semantic(
        mounted_semantic
    )
    counts = {
        "spatialPackCount": len(packs),
        "declaredSpatialRpfCount": declared_count,
        "nestedRpfResolvedCount": resolved_count,
        "ytypArtifactCount": sum(len(item["ytyps"]) for item in flattened),
        "ymapArtifactCount": sum(len(item["ymaps"]) for item in flattened),
        "ymfArtifactCount": sum(1 for item in effective_semantic if item[0] == "ymfs"),
        "discoveredYmfArtifactCount": sum(len(item["ymfs"]) for item in flattened),
        "equivalentDuplicateArtifactCount": len(equivalent_duplicates),
        "mloArchetypeCount": sum(
            len(ytyp["archetypes"]) for item in flattened for ytyp in item["ytyps"]
        ),
        "mloRoomCount": sum(
            len(arch["rooms"]) for item in flattened for ytyp in item["ytyps"]
            for arch in ytyp["archetypes"]
        ),
        "mloPortalCount": sum(
            len(arch["portals"]) for item in flattened for ytyp in item["ytyps"]
            for arch in ytyp["archetypes"]
        ),
        "mloInstanceCount": sum(
            len(ymap["instances"]) for item in flattened for ymap in item["ymaps"]
        ),
        "ymfRelationshipCount": sum(
            len(ymf["relationships"]) for item in flattened for ymf in item["ymfs"]
        ),
        "unsupportedOutcomeCount": len(unsupported) + len(equivalent_duplicates),
    }
    semantic_artifacts = []
    for family, item, _, _ in effective_semantic:
        status = {
            "ytyps": "consumed-mlo-archetypes",
            "ymaps": "consumed-mlo-instances",
            "ymfs": "consumed-spatial-manifest",
        }[family]
        semantic_artifacts.append(dict(item["artifact"], consumptionStatus=status))
    semantic_artifacts.sort(key=lambda item: item["sourceCoordinate"])
    normalized_unsupported = []
    for item in unsupported:
        detail = item.get("fieldLocator") or item.get("packIdentity") or item.get("exceptionType")
        normalized_unsupported.append({
            "sourceCoordinate": item.get("sourceCoordinate", dlclist_coordinate),
            "formatId": (
                MANIFEST["formats"]["nestedRpf"]["formatId"]
                if "rpf" in item["reasonCode"] or "container" in item["reasonCode"]
                else "rockstar.gta-v.mounted-spatial-source"
            ),
            "reasonCode": item["reasonCode"],
            "detailCode": detail or "no-additional-detail",
        })
    normalized_unsupported.extend(equivalent_duplicates)
    normalized_unsupported.sort(key=lambda item: (
        item["sourceCoordinate"], item["reasonCode"], item["detailCode"]
    ))
    index = {
        "schemaId": "grid.gta-v.spatial-corpus-index",
        "schemaVersion": INDEX_SCHEMA_VERSION,
        "decoder": {
            "methodId": "fivefury.gta-v.mounted-mlo-spatial",
            "exactVersion": lock["exactVersion"],
            "artifactSha256": lock["windowsX64WheelSha256"],
        },
        "mountGraph": {
            "mountScope": MANIFEST["mountScope"],
            "dlcListArtifact": dlclist_artifact,
            "packs": sorted(packs, key=lambda item: item["normalizedMountPath"]),
            "counts": counts,
            "unsupportedFamilies": MANIFEST["unsupportedFamilies"],
        },
        "artifacts": semantic_artifacts,
        "ytyps": sorted(
            (item for family, item, _, _ in effective_semantic if family == "ytyps"),
            key=lambda item: item["artifact"]["sourceCoordinate"],
        ),
        "ymaps": sorted(
            (item for family, item, _, _ in effective_semantic if family == "ymaps"),
            key=lambda item: item["artifact"]["sourceCoordinate"],
        ),
        "ymfs": sorted(
            (
                {"artifact": item["artifact"], "relations": item["relationships"]}
                for family, item, _, _ in effective_semantic if family == "ymfs"
            ),
            key=lambda item: item["artifact"]["sourceCoordinate"],
        ),
        "unsupported": normalized_unsupported,
    }
    expected = MANIFEST["exactBuildClosure"]
    if any(expected.values()) and counts != expected:
        raise ValueError(f"Spatial exact-build closure changed: {counts!r}")

    shadowed_coordinates = {
        item["sourceCoordinate"] for item in equivalent_duplicates
    }
    for value in containers.values():
        value["artifacts"] = [
            item for item in value["artifacts"]
            if item["sourceCoordinate"] not in shadowed_coordinates
        ]
    for coordinate in shadowed_coordinates:
        artifacts.pop(coordinate, None)
        if output is not None:
            frozen = _frozen_path(output, coordinate)
            if frozen.exists():
                frozen.unlink()

    for value in containers.values():
        value["artifacts"].sort(key=lambda item: item["sourceCoordinate"])
    receipt = {
        "schemaVersion": SCHEMA_VERSION,
        "gameId": GAME_ID,
        "gameVersionNamespace": "valve.steam.app.3240220.build-id",
        "gameVersion": platform_observation["buildId"],
        "platformObservation": platform_observation,
        "observedAtUtc": observed_at_utc,
        "acquisitionTool": {
            "toolId": "fivefury.rpf+ytyp+ymap+ymf-read",
            "exactVersion": lock["exactVersion"],
            "artifactSha256": lock["windowsX64WheelSha256"],
            "sourceRevision": lock["sourceRevision"],
            "methodId": "grid.gta-v-enhanced.mounted-spatial-acquisition",
            "methodVersion": "1",
            "receiptSchemaVersion": SCHEMA_VERSION,
        },
        "sourceFamilyManifest": {
            "manifestId": MANIFEST["manifestId"],
            "schemaVersion": 1,
            "documentSha256": MANIFEST_SHA256,
        },
        "mountClosure": counts,
        "containers": sorted(
            containers.values(), key=lambda item: item["containerCoordinate"]
        ),
    }
    index_document = _canonical_bytes(index) + b"\n"
    receipt["spatialCorpusIndex"] = {
        "fileName": INDEX_NAME,
        "schemaId": index["schemaId"],
        "schemaVersion": index["schemaVersion"],
            "decoderMethodId": index["decoder"]["methodId"],
        "decoderExactVersion": index["decoder"]["exactVersion"],
        "decoderArtifactSha256": index["decoder"]["artifactSha256"],
        "documentByteLength": len(index_document),
        "documentSha256": _sha256(index_document),
    }
    return receipt, index


def _validate_index_binding(binding: Any, document: bytes, lock: dict[str, Any]) -> None:
    parsed = json.loads(document.decode("utf-8", "strict"))
    if document != _canonical_bytes(parsed) + b"\n":
        raise ValueError("Spatial index is not canonical JSON plus LF")
    expected = {
        "fileName": INDEX_NAME,
        "schemaId": "grid.gta-v.spatial-corpus-index",
        "schemaVersion": 1,
        "decoderMethodId": "fivefury.gta-v.mounted-mlo-spatial",
        "decoderExactVersion": lock["exactVersion"],
        "decoderArtifactSha256": lock["windowsX64WheelSha256"],
        "documentByteLength": len(document),
        "documentSha256": _sha256(document),
    }
    if binding != expected:
        raise ValueError("Spatial index content or decoder binding mismatch")


def acquire(args: argparse.Namespace, lock: dict[str, Any]) -> None:
    output = Path(args.output).resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Spatial acquisition output must be absent or empty")
    output.mkdir(parents=True, exist_ok=True)
    game_root = Path(args.game_root).resolve(strict=True)
    observed = actor._validate_observed(args.observed_at_utc)
    receipt, index = _build_snapshot(
        game_root, lock, observed, actor._steam_observation(game_root), output
    )
    receipt["receiptDocumentSha256"] = _sha256(_canonical_bytes(receipt))
    (output / RECEIPT_NAME).write_bytes(_canonical_bytes(receipt) + b"\n")
    (output / INDEX_NAME).write_bytes(_canonical_bytes(index) + b"\n")


def verify(args: argparse.Namespace, lock: dict[str, Any]) -> None:
    receipt_path = Path(args.receipt).resolve(strict=True)
    root = receipt_path.parent
    receipt = json.loads(receipt_path.read_bytes().decode("utf-8", "strict"))
    digest = receipt.pop("receiptDocumentSha256", None)
    if digest != _sha256(_canonical_bytes(receipt)):
        raise ValueError("Spatial receipt document digest mismatch")
    if receipt.get("sourceFamilyManifest") != {
        "manifestId": MANIFEST["manifestId"],
        "schemaVersion": 1,
        "documentSha256": MANIFEST_SHA256,
    }:
        raise ValueError("Spatial receipt manifest binding mismatch")
    document = (root / INDEX_NAME).read_bytes()
    _validate_index_binding(receipt.get("spatialCorpusIndex"), document, lock)
    game_root = Path(args.game_root).resolve(strict=True)
    actual, index = _build_snapshot(
        game_root, lock, receipt["observedAtUtc"], actor._steam_observation(game_root), None
    )
    stable = json.loads(json.dumps(receipt))
    # Frozen paths are deterministic coordinates, so the in-memory reconstruction
    # is expected to be byte-equivalent apart from the receipt digest already removed.
    if actual != stable:
        raise ValueError("Spatial receipt does not match fresh source extraction")
    if document != _canonical_bytes(index) + b"\n":
        raise ValueError("Spatial index differs from fresh source extraction")
    for container in receipt["containers"]:
        path = actor._resolve_game_file(game_root, container["containerCoordinate"])
        length, sha = actor._sha256_file(path)
        if length != container["byteLength"] or sha != container["sha256"]:
            raise ValueError("Spatial source container bytes changed")
    for container in receipt["containers"]:
        for item in container["artifacts"]:
            relative = item.get("frozenRelativePath")
            if relative is None:
                continue
            frozen = root / PurePosixPath(relative)
            exact = frozen.read_bytes()
            if len(exact) != item["byteLength"] or _sha256(exact) != item["sha256"]:
                raise ValueError("Frozen spatial artifact bytes changed")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    acquire_parser = subparsers.add_parser("acquire")
    acquire_parser.add_argument("--game-root", required=True)
    acquire_parser.add_argument("--output", required=True)
    acquire_parser.add_argument("--observed-at-utc", required=True)
    acquire_parser.add_argument("--fivefury-wheel", required=True)
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("--game-root", required=True)
    verify_parser.add_argument("--receipt", required=True)
    verify_parser.add_argument("--fivefury-wheel", required=True)
    args = parser.parse_args()
    lock = actor._load_lock(Path(args.fivefury_wheel).resolve(strict=True))
    if args.command == "acquire":
        acquire(args, lock)
    else:
        verify(args, lock)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
