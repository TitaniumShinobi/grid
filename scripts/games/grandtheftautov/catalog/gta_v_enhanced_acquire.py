"""Read-only, preproduction GTA V Enhanced RPF member acquisition.

This tool deliberately has no GRID runtime authority. It hashes each original
container, reads exact members through the digest-pinned FiveFury dependency,
and emits a deterministic receipt plus frozen member files. It never emits game
keys or local installation paths into the receipt.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import re
from datetime import datetime
from pathlib import Path, PurePosixPath
from typing import Any, Iterable

from fivefury.crypto import GameCrypto
from fivefury.rpf import RpfArchive, RpfFileEntry


SCHEMA_VERSION = 2
LEGACY_SCHEMA_VERSION = 1
GAME_ID = "game.grandtheftautov-enhanced"
STEAM_APP_ID = "3240220"
LOCK_PATH = Path(__file__).with_name("fivefury.lock.v1.json")
SOURCE_FAMILY_MANIFEST_PATH = Path(__file__).with_name(
    "gta_v_enhanced_source_families.v2.json"
)
EXPECTED_SOURCE_FAMILY_MANIFEST_SHA256 = (
    "a477091b010d4a80958a77a35247a520cf98b8df4d8b8c27c76c979e8be03338"
)
LEGACY_SOURCE_FAMILY_MANIFEST_SHA256 = "426e2559aa88b79420d65508db0c8955795805dca6c37b90993bc88b62d81be4"
EXPECTED_FIXED_SOURCE_DECLARATIONS = {
    ("common.rpf", "data/ai/weapons.meta"): (
        "rockstar.gta-v.enhanced.weapons-meta",
        "rockstar.gta-v.weapons-meta.cweaponinfoblob-xml",
        "1",
    ),
    ("common.rpf", "data/levels/gta5/mapzones.xml"): (
        "rockstar.gta-v.enhanced.mapzones",
        "rockstar.gta-v.mapzones.cmapzonescontainer-xml",
        "1",
    ),
    ("update/update.rpf", "common/data/ai/ambientpedmodelsets.meta"): (
        "rockstar.gta-v.enhanced.ambient-ped-model-sets",
        "rockstar.gta-v.ambient-ped-model-sets-xml",
        "1",
    ),
    ("update/update.rpf", "common/data/gen9_exclusive_assets_peds.meta"): (
        "rockstar.gta-v.enhanced.gen9-exclusive-peds",
        "rockstar.gta-v.gen9-exclusive-assets-peds-xml",
        "1",
    ),
    ("update/update.rpf", "common/data/levels/gta5/popzone.ipl"): (
        "rockstar.gta-v.enhanced.population-zones",
        "rockstar.gta-v.population-zones-ipl",
        "1",
    ),
    ("update/update.rpf", "x64/data/cdimages/scaleform_generic.rpf!/hud.gfx"): (
        "rockstar.gta-v.enhanced.hud-gfx",
        "rockstar.scaleform.gfx-v8",
        "1",
    ),
    ("update/update.rpf", "x64/patch/data/lang/american_rel.rpf"): (
        "rockstar.gta-v.enhanced.nested-localization-containers",
        "rockstar.rpf7-container",
        "1",
    ),
    ("update/update.rpf", "x64/patch/data/lang/american_rel.rpf!/global.gxt2"): (
        "rockstar.gta-v.enhanced.patch-american-localization",
        "rockstar.gta-v.gxt2-binary",
        "1",
    ),
    ("x64b.rpf", "data/lang/american_rel.rpf"): (
        "rockstar.gta-v.enhanced.nested-localization-containers",
        "rockstar.rpf7-container",
        "1",
    ),
    ("x64b.rpf", "data/lang/american_rel.rpf!/global.gxt2"): (
        "rockstar.gta-v.enhanced.base-american-localization",
        "rockstar.gta-v.gxt2-binary",
        "1",
    ),
}
EXPECTED_DYNAMIC_SOURCE_DECLARATION = (
    "update/update2.rpf",
    "common/data/ugc/",
    ".ugc",
    1052,
    "rockstar.gta-v.enhanced.ugc-missions",
    "rockstar.gta-v.ugc.mission-json",
    "1",
)
EXPECTED_COMMON_DYNAMIC_SOURCE_DECLARATION = (
    "common.rpf",
    "data/ugc/",
    ".ugc",
    40,
    "rockstar.gta-v.enhanced.online-activity-registry",
    "rockstar.gta-v.ugc.mission-json",
    "1",
)


def _sha256_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    length = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            length += len(chunk)
            digest.update(chunk)
    return length, digest.hexdigest()


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _strict_text(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value or value.strip() != value:
        raise ValueError(f"{name} must be nonempty exact text without surrounding whitespace")
    value.encode("utf-8", "strict")
    return value


def _normalize_member(value: str) -> str:
    value = _strict_text(value.replace("\\", "/"), "member coordinate")
    normalized = str(PurePosixPath(value))
    if normalized != value or value.startswith("/") or ".." in PurePosixPath(value).parts:
        raise ValueError(f"Noncanonical member coordinate: {value}")
    return value


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8", "strict")


def _require_exact_keys(value: dict[str, Any], required: set[str], optional: set[str], name: str) -> None:
    actual = set(value)
    if not required.issubset(actual) or not actual.issubset(required | optional):
        raise ValueError(f"{name} has missing or unsupported fields")


def _validate_dynamic_declaration(
    coordinate: str, dynamic: dict[str, Any], family_status: dict[str, str]
) -> None:
    if not isinstance(dynamic, dict):
        raise ValueError("Dynamic member declaration must be one object")
    _require_exact_keys(
        dynamic,
        {"prefix", "suffix", "exactCount", "sourceFamilyId", "formatId", "exactFormatVersion"},
        set(),
        "dynamic member family",
    )
    prefix = _strict_text(dynamic["prefix"], "dynamic member prefix")
    if not prefix.endswith("/") or _normalize_member(prefix[:-1]) + "/" != prefix:
        raise ValueError("Dynamic member prefix must end with a slash")
    family_id = _strict_text(dynamic["sourceFamilyId"], "dynamic source family ID")
    if family_status.get(family_id) != "supported":
        raise ValueError("Dynamic UGC family must be supported")
    declaration = (
        coordinate,
        prefix,
        dynamic["suffix"],
        dynamic["exactCount"],
        family_id,
        _strict_text(dynamic["formatId"], "dynamic format ID"),
        _strict_text(dynamic["exactFormatVersion"], "dynamic exact format version"),
    )
    if declaration not in (EXPECTED_DYNAMIC_SOURCE_DECLARATION, EXPECTED_COMMON_DYNAMIC_SOURCE_DECLARATION):
        raise ValueError("Dynamic source coordinate has an unapproved family or format tuple")


def _load_source_family_manifest(path: Path = SOURCE_FAMILY_MANIFEST_PATH) -> tuple[dict[str, Any], str]:
    """Load the exact-build acquisition registry and reject ambiguous declarations."""
    exact_bytes = path.read_bytes()
    manifest = json.loads(exact_bytes.decode("utf-8", "strict"))
    if not isinstance(manifest, dict):
        raise ValueError("Source-family manifest must be one JSON object")
    _require_exact_keys(
        manifest,
        {"schemaVersion", "manifestId", "gameId", "steamAppId", "steamBuildId", "sourceFamilies", "containers"},
        set(),
        "source-family manifest",
    )
    if (
        manifest["schemaVersion"] != 2
        or manifest["manifestId"] != "grid.gta-v-enhanced.source-families"
        or manifest["gameId"] != GAME_ID
        or manifest["steamAppId"] != STEAM_APP_ID
        or manifest["steamBuildId"] != "25261616"
    ):
        raise ValueError("Source-family manifest identity mismatch")

    valid_statuses = {"supported", "indexed-unconsumed", "diagnostic", "unsupported-declared"}
    valid_kinds = {"Location", "MissionQuest", "Item", "Actor"}
    families = manifest["sourceFamilies"]
    if not isinstance(families, list) or not families:
        raise ValueError("Source-family manifest has no source families")
    family_ids: list[str] = []
    family_status: dict[str, str] = {}
    for family in families:
        if not isinstance(family, dict):
            raise ValueError("Source-family entry must be one object")
        _require_exact_keys(
            family,
            {"sourceFamilyId", "status", "knowledgeKinds"},
            {"reasonCode", "potentialCoverage"},
            "source-family entry",
        )
        family_id = _strict_text(family["sourceFamilyId"], "source family ID")
        status = _strict_text(family["status"], "source family status")
        kinds = family["knowledgeKinds"]
        if status not in valid_statuses:
            raise ValueError(f"Unsupported source-family status: {status}")
        if (
            not isinstance(kinds, list)
            or not kinds
            or kinds != sorted(kinds)
            or len(kinds) != len(set(kinds))
            or any(kind not in valid_kinds for kind in kinds)
        ):
            raise ValueError(f"Source family has invalid knowledge kinds: {family_id}")
        if status != "supported" and "reasonCode" not in family:
            raise ValueError(f"Nonsupported source family requires a reason code: {family_id}")
        if "reasonCode" in family:
            _strict_text(family["reasonCode"], "source family reason code")
        if "potentialCoverage" in family:
            _strict_text(family["potentialCoverage"], "source family potential coverage")
        family_ids.append(family_id)
        family_status[family_id] = status
    if family_ids != sorted(family_ids) or len(family_ids) != len(set(family_ids)):
        raise ValueError("Source families must be unique and ordinally sorted")

    containers = manifest["containers"]
    if not isinstance(containers, list) or len(containers) != 4:
        raise ValueError("Source-family manifest requires exactly four containers")
    coordinates: list[str] = []
    fixed_count = 0
    dynamic_count = 0
    for container in containers:
        if not isinstance(container, dict):
            raise ValueError("Source container declaration must be one object")
        coordinate = _normalize_member(container.get("containerCoordinate"))
        mode = container.get("memberMode")
        coordinates.append(coordinate)
        if mode in ("fixed", "fixed-and-dynamic-prefix"):
            required = {"containerCoordinate", "memberMode", "members"}
            if mode == "fixed-and-dynamic-prefix":
                required.add("dynamicMembers")
            _require_exact_keys(container, required, set(), "fixed container")
            members = container["members"]
            if not isinstance(members, list) or not members:
                raise ValueError(f"Fixed source container has no members: {coordinate}")
            paths: list[str] = []
            for member in members:
                if not isinstance(member, dict):
                    raise ValueError("Fixed member declaration must be one object")
                _require_exact_keys(
                    member,
                    {"memberPath", "sourceFamilyId", "formatId", "exactFormatVersion"},
                    set(),
                    "fixed member",
                )
                member_path = _normalize_member(member["memberPath"])
                family_id = _strict_text(member["sourceFamilyId"], "member source family ID")
                if family_id not in family_status or family_status[family_id] == "unsupported-declared":
                    raise ValueError(f"Acquired member references an invalid source family: {family_id}")
                format_id = _strict_text(member["formatId"], "member format ID")
                format_version = _strict_text(
                    member["exactFormatVersion"], "member exact format version"
                )
                expected = EXPECTED_FIXED_SOURCE_DECLARATIONS.get(
                    (coordinate, member_path)
                )
                if expected != (family_id, format_id, format_version):
                    raise ValueError(
                        "Fixed source coordinate has an unapproved family or format tuple: "
                        f"{coordinate}!/{member_path}"
                    )
                paths.append(member_path)
            if paths != sorted(paths) or len(paths) != len(set(paths)):
                raise ValueError(f"Fixed members must be unique and ordinally sorted: {coordinate}")
            fixed_count += len(paths)
            if mode == "fixed-and-dynamic-prefix":
                dynamic = container["dynamicMembers"]
                _validate_dynamic_declaration(coordinate, dynamic, family_status)
                dynamic_count += 1
        elif mode == "dynamic-prefix":
            _require_exact_keys(container, {"containerCoordinate", "memberMode", "dynamicMembers"}, set(), "dynamic container")
            dynamic = container["dynamicMembers"]
            if not isinstance(dynamic, dict):
                raise ValueError("Dynamic member declaration must be one object")
            _require_exact_keys(
                dynamic,
                {"prefix", "suffix", "exactCount", "sourceFamilyId", "formatId", "exactFormatVersion"},
                set(),
                "dynamic member family",
            )
            _validate_dynamic_declaration(coordinate, dynamic, family_status)
            dynamic_count += 1
        else:
            raise ValueError(f"Unsupported source member mode: {mode}")
    if coordinates != sorted(coordinates) or len(coordinates) != len(set(coordinates)):
        raise ValueError("Source containers must be unique and ordinally sorted")
    if fixed_count != 10 or dynamic_count != 2:
        raise ValueError("Source-family manifest requires ten fixed members and two dynamic UGC families")
    canonical_digest = _sha256_bytes(_canonical_bytes(manifest))
    if canonical_digest != EXPECTED_SOURCE_FAMILY_MANIFEST_SHA256:
        raise ValueError("Source-family manifest content does not match the approved v2 registry")
    return manifest, canonical_digest


SOURCE_FAMILY_MANIFEST, SOURCE_FAMILY_MANIFEST_SHA256 = _load_source_family_manifest()
FIXED_MEMBERS = {
    container["containerCoordinate"]: tuple(member["memberPath"] for member in container["members"])
    for container in SOURCE_FAMILY_MANIFEST["containers"]
    if container["memberMode"] in ("fixed", "fixed-and-dynamic-prefix")
}
DYNAMIC_MEMBERS = {
    container["containerCoordinate"]: container["dynamicMembers"]
    for container in SOURCE_FAMILY_MANIFEST["containers"]
    if container["memberMode"] in ("dynamic-prefix", "fixed-and-dynamic-prefix")
}
CONTAINER_COORDINATES = tuple(
    container["containerCoordinate"] for container in SOURCE_FAMILY_MANIFEST["containers"]
)


def _load_lock(wheel: Path) -> dict[str, Any]:
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    if lock.get("schemaVersion") != 1 or lock.get("package") != "fivefury":
        raise ValueError("Unsupported FiveFury lock document")
    installed = importlib.metadata.version("fivefury")
    if installed != lock["exactVersion"]:
        raise ValueError(
            f"FiveFury version mismatch: expected {lock['exactVersion']}, found {installed}"
        )
    _, wheel_digest = _sha256_file(wheel)
    if wheel.name != lock["windowsX64Wheel"] or wheel_digest != lock["windowsX64WheelSha256"]:
        raise ValueError("FiveFury wheel name or SHA-256 does not match the checked-in lock")
    return lock


def _resolve_container(game_root: Path, coordinate: str) -> Path:
    parts = PurePosixPath(coordinate).parts
    result = game_root.joinpath(*parts).resolve(strict=True)
    root = game_root.resolve(strict=True)
    if root not in result.parents:
        raise ValueError("Container escaped the exact game root")
    if not result.is_file():
        raise ValueError(f"Container is not a file: {coordinate}")
    return result


def _steam_observation(game_root: Path) -> dict[str, Any]:
    steamapps = game_root.parent.parent
    manifest = (steamapps / f"appmanifest_{STEAM_APP_ID}.acf").resolve(strict=True)
    if manifest.parent != steamapps.resolve(strict=True):
        raise ValueError("Steam app manifest escaped the installation's steamapps directory")
    exact_bytes = manifest.read_bytes()
    text = exact_bytes.decode("utf-8", "strict")
    app_id, build_id, install_dir = _parse_app_state_identity(text)
    if app_id != STEAM_APP_ID or install_dir != game_root.name:
        raise ValueError("Steam manifest app or installation identity mismatch")
    return {
        "provider": "valve.steam",
        "appId": app_id,
        "buildId": build_id,
        "installDir": install_dir,
        "manifestCoordinate": f"steamapps/appmanifest_{STEAM_APP_ID}.acf",
        "manifestByteLength": len(exact_bytes),
        "manifestSha256": _sha256_bytes(exact_bytes),
        "appIdFieldPath": "/AppState/appid",
        "buildIdFieldPath": "/AppState/buildid",
        "installDirFieldPath": "/AppState/installdir",
    }


def _parse_app_state_identity(text: str) -> tuple[str, str, str]:
    """Parse only the exact stable direct members claimed by /AppState paths."""
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if len(lines) < 3 or lines[0] != '"AppState"' or lines[1] != "{":
        raise ValueError("Steam manifest requires one exact AppState root object")
    stable_names = ("appid", "buildid", "installdir")
    stable: dict[str, str] = {}
    depth = 1
    expect_object_open = False
    root_closed = False
    for index, line in enumerate(lines[2:], start=2):
        if root_closed:
            raise ValueError("Steam manifest has content after the AppState root")
        if line == "{":
            if not expect_object_open:
                raise ValueError("Steam manifest has an unbound object opening")
            depth += 1
            expect_object_open = False
            continue
        if expect_object_open:
            raise ValueError("Steam manifest object name is not followed by an opening brace")
        if line == "}":
            depth -= 1
            if depth < 0:
                raise ValueError("Steam manifest has an unmatched closing brace")
            if depth == 0:
                root_closed = True
            continue
        pair = re.fullmatch(r'"([^"\r\n]+)"\s+"([^"\r\n]*)"', line)
        if pair is not None:
            name, value = pair.groups()
            matching = next((item for item in stable_names if item.lower() == name.lower()), None)
            if matching is not None:
                if name != matching or depth != 1 or matching in stable or not value:
                    raise ValueError(
                        f"Steam manifest requires exactly one nonempty direct exact-case {matching} field"
                    )
                stable[matching] = value
            continue
        object_name = re.fullmatch(r'"([^"\r\n]+)"', line)
        if object_name is None:
            raise ValueError(f"Steam manifest has unsupported structure at line {index + 1}")
        if any(item.lower() == object_name.group(1).lower() for item in stable_names):
            raise ValueError("A stable Steam identity field cannot be an object")
        expect_object_open = True
    if expect_object_open or depth != 0 or not root_closed:
        raise ValueError("Steam manifest AppState object is not closed")
    if set(stable) != set(stable_names):
        raise ValueError("Steam manifest is missing a stable AppState identity field")
    return stable["appid"], stable["buildid"], stable["installdir"]


def _verify_legacy_platform_observation(
    game_root: Path, expected: dict[str, Any]
) -> None:
    """Replay schema v1 exactly: immutable whole-manifest bytes, without v2 reinterpretation."""
    if not isinstance(expected, dict):
        raise ValueError("Steam platform observation is missing")
    required = {
        "provider": "valve.steam",
        "appId": STEAM_APP_ID,
        "manifestCoordinate": f"steamapps/appmanifest_{STEAM_APP_ID}.acf",
        "appIdFieldPath": "/AppState/appid",
        "buildIdFieldPath": "/AppState/buildid",
        "installDirFieldPath": "/AppState/installdir",
    }
    if any(expected.get(name) != value for name, value in required.items()):
        raise ValueError("Legacy Steam platform observation coordinate mismatch")
    _strict_text(expected.get("buildId"), "legacy Steam build ID")
    length = expected.get("manifestByteLength")
    digest = expected.get("manifestSha256")
    if not isinstance(length, int) or length <= 0:
        raise ValueError("Legacy Steam manifest length is invalid")
    if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
        raise ValueError("Legacy Steam manifest digest is invalid")
    steamapps = game_root.parent.parent
    manifest = (steamapps / f"appmanifest_{STEAM_APP_ID}.acf").resolve(strict=True)
    if manifest.parent != steamapps.resolve(strict=True):
        raise ValueError("Steam app manifest escaped the installation's steamapps directory")
    actual_length, actual_digest = _sha256_file(manifest)
    if actual_length != length or actual_digest != digest:
        raise ValueError("Steam app/build observation does not match the local app manifest")


def _entry_bytes(archive: RpfArchive, member: str) -> bytes:
    """Read an exact member, preserving every explicit nested-RPF boundary."""
    components = member.split("!/")
    current = archive
    nested: list[RpfArchive] = []
    try:
        for index, component in enumerate(components):
            entry = current.find_entry(component)
            if not isinstance(entry, RpfFileEntry):
                raise ValueError(f"Exact RPF member was not found: {member}")
            exact_bytes = current.read_entry_bytes(entry, logical=True)
            if index == len(components) - 1:
                return exact_bytes
            if not component.endswith(".rpf"):
                raise ValueError(f"Nested archive boundary is not an exact RPF member: {component}")
            current = RpfArchive.from_bytes(
                exact_bytes,
                name=PurePosixPath(component).name,
                source_path=component,
                crypto=archive.crypto,
            )
            nested.append(current)
    finally:
        for nested_archive in reversed(nested):
            nested_archive.close()
    raise ValueError(f"Exact RPF member was not found: {member}")


def _ugc_members(archive: RpfArchive, prefix: str, suffix: str, exact_count: int) -> list[str]:
    result = []
    for entry in archive.iter_entries():
        if not isinstance(entry, RpfFileEntry):
            continue
        member = _normalize_member(entry.full_path.replace("\\", "/").lstrip("/"))
        if member.startswith(prefix) and member.endswith(suffix):
            result.append(member)
    result.sort()
    if len(result) != exact_count or len(result) != len(set(result)):
        raise ValueError(
            f"UGC member discovery must contain exactly {exact_count} unique members"
        )
    return result


def _receipt_member_paths(container: dict[str, Any]) -> list[str]:
    members = container.get("members")
    if not isinstance(members, list) or not members:
        raise ValueError("Container receipt has no members")
    paths: list[str] = []
    coordinates: list[str] = []
    container_coordinate = _strict_text(container.get("containerCoordinate"), "container coordinate")
    for member in members:
        if not isinstance(member, dict):
            raise ValueError("Receipt member must be one object")
        path = _normalize_member(member.get("memberPath"))
        coordinate = _strict_text(member.get("memberCoordinate"), "member coordinate")
        if coordinate != f"{container_coordinate}!/{path}":
            raise ValueError("Receipt member coordinate mismatch")
        paths.append(path)
        coordinates.append(coordinate)
    if coordinates != sorted(coordinates) or len(coordinates) != len(set(coordinates)):
        raise ValueError("Receipt members must be unique and ordinally sorted")
    return paths


def _validate_receipt_closure(receipt: dict[str, Any]) -> None:
    """Require either the historical closure or the manifest-bound current closure."""
    containers = receipt.get("containers")
    if not isinstance(containers, list) or len(containers) != 4:
        raise ValueError("Acquisition receipt requires exactly four containers")
    coordinates = [
        _strict_text(container.get("containerCoordinate"), "container coordinate")
        for container in containers
        if isinstance(container, dict)
    ]
    if len(coordinates) != 4 or coordinates != sorted(coordinates) or len(set(coordinates)) != 4:
        raise ValueError("Receipt containers must be unique and ordinally sorted")

    binding = receipt.get("sourceFamilyManifest")
    current = binding == {
            "manifestId": SOURCE_FAMILY_MANIFEST["manifestId"],
            "schemaVersion": SOURCE_FAMILY_MANIFEST["schemaVersion"],
            "documentSha256": SOURCE_FAMILY_MANIFEST_SHA256,
        }
    legacy_v1 = binding == {
        "manifestId": "grid.gta-v-enhanced.source-families",
        "schemaVersion": 1,
        "documentSha256": LEGACY_SOURCE_FAMILY_MANIFEST_SHA256,
    }
    if binding is not None and not current and not legacy_v1:
            raise ValueError("Receipt source-family manifest binding mismatch")

    expected_fixed = {
        coordinate: tuple(paths)
        for coordinate, paths in FIXED_MEMBERS.items()
    }
    if binding is None:
        # Historical schema-v1/v2 receipts predate diagnostic hud.gfx acquisition.
        expected_fixed["update/update.rpf"] = tuple(
            path for path in expected_fixed["update/update.rpf"]
            if path != "x64/data/cdimages/scaleform_generic.rpf!/hud.gfx"
        )

    expected_coordinates = tuple(sorted(set(expected_fixed) | set(DYNAMIC_MEMBERS)))
    if tuple(coordinates) != expected_coordinates:
        raise ValueError("Receipt container closure does not match the source-family manifest")
    total = 0
    for container in containers:
        coordinate = container["containerCoordinate"]
        paths = _receipt_member_paths(container)
        if coordinate in DYNAMIC_MEMBERS:
            dynamic = DYNAMIC_MEMBERS[coordinate]
            dynamic_paths = [path for path in paths if path.startswith(dynamic["prefix"]) and path.endswith(dynamic["suffix"])]
            fixed_paths = [path for path in paths if path not in dynamic_paths]
            expected_dynamic_count = dynamic["exactCount"] if current else (1052 if coordinate == "update/update2.rpf" else 0)
            if (
                len(dynamic_paths) != expected_dynamic_count
                or tuple(fixed_paths) != tuple(sorted(expected_fixed.get(coordinate, ())))
            ):
                raise ValueError("Receipt UGC closure does not match the source-family manifest")
        elif tuple(paths) != tuple(sorted(expected_fixed[coordinate])):
            raise ValueError(f"Receipt fixed-member closure mismatch: {coordinate}")
        total += len(paths)
    expected_total = 1102 if current else (1062 if legacy_v1 else 1061)
    if total != expected_total:
        raise ValueError(f"Receipt must contain exactly {expected_total} frozen members")


def _member_output(root: Path, container: str, member: str) -> Path:
    return root.joinpath("members", *PurePosixPath(container).parts, *PurePosixPath(member).parts)


def _container_members(archive: RpfArchive, coordinate: str) -> Iterable[str]:
    fixed = list(FIXED_MEMBERS.get(coordinate, ()))
    if coordinate in DYNAMIC_MEMBERS:
        dynamic = DYNAMIC_MEMBERS[coordinate]
        fixed.extend(_ugc_members(archive, dynamic["prefix"], dynamic["suffix"], dynamic["exactCount"]))
        return sorted(fixed)
    if fixed:
        return fixed
    raise ValueError(f"Unsupported container coordinate: {coordinate}")


def acquire(args: argparse.Namespace, lock: dict[str, Any]) -> None:
    output = Path(args.output).resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Acquisition output directory must be absent or empty")
    output.mkdir(parents=True, exist_ok=True)
    game_root = Path(args.game_root).resolve(strict=True)
    platform_observation = _steam_observation(game_root)
    observed = datetime.fromisoformat(args.observed_at_utc.replace("Z", "+00:00"))
    if observed.utcoffset() is None or observed.utcoffset().total_seconds() != 0:
        raise ValueError("--observed-at-utc must be an exact UTC timestamp")

    crypto = GameCrypto.from_game(game_root, gen9=True, use_cache=False)
    containers = []
    for coordinate in CONTAINER_COORDINATES:
        path = _resolve_container(game_root, coordinate)
        container_length, container_digest = _sha256_file(path)
        archive = RpfArchive.from_path(path, crypto=crypto)
        members = []
        try:
            for member in _container_members(archive, coordinate):
                member = _normalize_member(member)
                exact_bytes = _entry_bytes(archive, member)
                target = _member_output(output, coordinate, member)
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("xb") as stream:
                    stream.write(exact_bytes)
                members.append(
                    {
                        "memberCoordinate": f"{coordinate}!/{member}",
                        "memberPath": member,
                        "byteLength": len(exact_bytes),
                        "sha256": _sha256_bytes(exact_bytes),
                    }
                )
        finally:
            archive.close()
        containers.append(
            {
                "containerCoordinate": coordinate,
                "byteLength": container_length,
                "sha256": container_digest,
                "members": sorted(members, key=lambda value: value["memberCoordinate"]),
            }
        )

    receipt: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "gameId": GAME_ID,
        "gameVersionNamespace": "valve.steam.app.3240220.build-id",
        "gameVersion": platform_observation["buildId"],
        "platformObservation": platform_observation,
        "observedAtUtc": observed.isoformat().replace("+00:00", "Z"),
        "acquisitionTool": {
            "toolId": "fivefury.rpf-read",
            "exactVersion": lock["exactVersion"],
            "artifactSha256": lock["windowsX64WheelSha256"],
            "sourceRevision": lock["sourceRevision"],
            "methodId": lock["acquisitionMethodId"],
            "methodVersion": lock["acquisitionMethodVersion"],
            "receiptSchemaVersion": SCHEMA_VERSION,
        },
        "sourceFamilyManifest": {
            "manifestId": SOURCE_FAMILY_MANIFEST["manifestId"],
            "schemaVersion": SOURCE_FAMILY_MANIFEST["schemaVersion"],
            "documentSha256": SOURCE_FAMILY_MANIFEST_SHA256,
        },
        "containers": sorted(containers, key=lambda value: value["containerCoordinate"]),
    }
    receipt["receiptDocumentSha256"] = _sha256_bytes(_canonical_bytes(receipt))
    receipt_path = output / "gta-v-enhanced-acquisition-receipt.v2.json"
    receipt_path.write_bytes(_canonical_bytes(receipt) + b"\n")


def verify(args: argparse.Namespace, lock: dict[str, Any]) -> None:
    receipt_path = Path(args.receipt).resolve(strict=True)
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    document_digest = receipt.pop("receiptDocumentSha256", None)
    if document_digest != _sha256_bytes(_canonical_bytes(receipt)):
        raise ValueError("Acquisition receipt document digest mismatch")
    schema_version = receipt.get("schemaVersion")
    if schema_version not in (LEGACY_SCHEMA_VERSION, SCHEMA_VERSION) or receipt.get("gameId") != GAME_ID:
        raise ValueError("Acquisition receipt schema or GameId mismatch")
    _validate_receipt_closure(receipt)
    method_version = (
        lock["legacyReceiptV1MethodVersion"]
        if schema_version == LEGACY_SCHEMA_VERSION
        else lock["acquisitionMethodVersion"]
    )
    tool = receipt.get("acquisitionTool", {})
    expected_tool = {
        "toolId": "fivefury.rpf-read",
        "exactVersion": lock["exactVersion"],
        "artifactSha256": lock["windowsX64WheelSha256"],
        "sourceRevision": lock["sourceRevision"],
        "methodId": lock["acquisitionMethodId"],
        "methodVersion": method_version,
        "receiptSchemaVersion": schema_version,
    }
    if tool != expected_tool:
        raise ValueError("Acquisition tool coordinate does not match the checked-in lock")

    game_root = Path(args.game_root).resolve(strict=True)
    expected_platform = receipt.get("platformObservation")
    if schema_version == LEGACY_SCHEMA_VERSION:
        # Schema v1 intentionally retains its original exact whole-manifest replay semantics.
        _verify_legacy_platform_observation(game_root, expected_platform)
    else:
        actual_platform = _steam_observation(game_root)
        if not isinstance(expected_platform, dict):
            raise ValueError("Steam platform observation is missing")
        stable_fields = (
            "provider", "appId", "buildId", "installDir",
            "manifestCoordinate", "appIdFieldPath", "buildIdFieldPath", "installDirFieldPath",
        )
        if any(expected_platform.get(field) != actual_platform[field] for field in stable_fields):
            raise ValueError("Stable Steam app/build/install observation does not match the local app manifest")
        # The exact observed bytes remain immutable historical audit evidence in the receipt.
        if not isinstance(expected_platform.get("manifestByteLength"), int) or expected_platform["manifestByteLength"] <= 0:
            raise ValueError("Historical Steam manifest length is invalid")
        manifest_digest = expected_platform.get("manifestSha256")
        if not isinstance(manifest_digest, str) or not re.fullmatch(r"[0-9a-f]{64}", manifest_digest):
            raise ValueError("Historical Steam manifest digest is invalid")
    if receipt.get("gameVersion") != expected_platform["buildId"]:
        raise ValueError("Receipt game version is not bound to the observed Steam build")
    crypto = GameCrypto.from_game(game_root, gen9=True, use_cache=False)
    acquisition_root = receipt_path.parent
    containers = receipt.get("containers")
    if not isinstance(containers, list) or not containers:
        raise ValueError("Acquisition receipt has no containers")
    for item in containers:
        coordinate = _strict_text(item.get("containerCoordinate"), "container coordinate")
        path = _resolve_container(game_root, coordinate)
        length, digest = _sha256_file(path)
        if length != item.get("byteLength") or digest != item.get("sha256"):
            raise ValueError(f"Container digest or length mismatch: {coordinate}")
        archive = RpfArchive.from_path(path, crypto=crypto)
        try:
            members = item.get("members")
            if not isinstance(members, list) or not members:
                raise ValueError(f"Container receipt has no members: {coordinate}")
            for member_item in members:
                member = _normalize_member(member_item.get("memberPath"))
                if member_item.get("memberCoordinate") != f"{coordinate}!/{member}":
                    raise ValueError("Receipt member coordinate mismatch")
                exact_bytes = _entry_bytes(archive, member)
                if len(exact_bytes) != member_item.get("byteLength") or _sha256_bytes(exact_bytes) != member_item.get("sha256"):
                    raise ValueError(f"Re-extracted member digest or length mismatch: {member}")
                frozen = _member_output(acquisition_root, coordinate, member)
                frozen_length, frozen_digest = _sha256_file(frozen)
                if frozen_length != len(exact_bytes) or frozen_digest != _sha256_bytes(exact_bytes):
                    raise ValueError(f"Frozen member differs from the verified container member: {member}")
        finally:
            archive.close()


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
