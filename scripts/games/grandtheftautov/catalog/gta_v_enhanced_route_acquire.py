"""Bounded shipped YND street-label acquisition; never changes the game or GRID store.

The sidecar retains every native node but is derived indexing data. Canonical names
are joined again from frozen GXT2 bytes by the knowledge adapter. This family makes
no claim about effective mounted-road closure or geographic containment.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any

from fivefury.crypto import GameCrypto
from fivefury.gxt2 import read_gxt2
from fivefury.rpf import RpfArchive, RpfFileEntry
from fivefury.ynd import read_ynd

import gta_v_enhanced_actor_acquire as actor

MANIFEST_PATH = Path(__file__).with_name("gta_v_enhanced_route_source_families.v1.json")
MANIFEST = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
RECEIPT_NAME = "gta-v-enhanced-route-acquisition-receipt.v1.json"
INDEX_NAME = "route-corpus-index.v1.json"
YND_FORMAT = "rockstar.gta-v.ynd-rsc7"
GXT_FORMAT = "rockstar.gta-v.gxt2-binary"
RPF_FORMAT = "rockstar.rpf7-container"
DECODER_METHOD = "fivefury.gta-v.update-paths-named-routes"
MAX_SOURCE_BYTES = 64 * 1024 * 1024


def canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _parse_ynd(exact: bytes) -> list[dict[str, int]]:
    if len(exact) > MAX_SOURCE_BYTES:
        raise ValueError("Route YND exceeds the bounded artifact size")
    resource = read_ynd(exact)
    if resource.version != 1:
        raise ValueError("Unsupported YND resource version")
    nodes = []
    identities = set()
    for ordinal, node in enumerate(resource.nodes):
        identity = (int(node.area_id), int(node.node_id))
        if identity in identities:
            raise ValueError("One YND contains duplicate native node identities")
        identities.add(identity)
        nodes.append({"ordinal": ordinal, "areaId": identity[0], "nodeId": identity[1],
                      "streetNameHash": int(node.street_name_hash)})
    return nodes


def _gxt_keys(exact: bytes) -> set[int]:
    if len(exact) > MAX_SOURCE_BYTES:
        raise ValueError("Route GXT2 exceeds the bounded artifact size")
    entries = read_gxt2(exact).entries
    keys = [entry.hash for entry in entries]
    if len(keys) != len(set(keys)):
        raise ValueError("Duplicate GXT2 keys are ambiguous")
    return {entry.hash for entry in entries if entry.text and not any(ord(c) < 32 for c in entry.text)}


def _counts(ynds: list[dict], gxt: bytes) -> dict[str, int]:
    hashes = {node["streetNameHash"] for source in ynds for node in source["nodes"]} - {0}
    named = hashes & _gxt_keys(gxt)
    return {"yndArtifactCount": len(ynds), "nodeCount": sum(len(s["nodes"]) for s in ynds),
            "distinctNonzeroHashes": len(hashes), "namedRouteCount": len(named),
            "unmatchedHashCount": len(hashes - named)}


def _frozen_relative(coordinate: str) -> str:
    actor._normalize_relative(coordinate.replace("!/", "/"), "source coordinate")
    return "members/" + coordinate.replace("!/", ".container/")


def _build_snapshot(game_root: Path, lock: dict, observed: str, output: Path | None):
    platform = actor._steam_observation(game_root)
    if platform["buildId"] != MANIFEST["steamBuildId"]:
        raise ValueError("Route source build differs from the admitted manifest")
    crypto = GameCrypto.from_game(game_root, gen9=True, use_cache=False)
    containers: dict[str, dict] = {}
    artifacts: list[dict] = []

    def outer(coordinate: str):
        path = actor._resolve_game_file(game_root, coordinate)
        size, digest = actor._sha256_file(path)
        containers[coordinate] = {"containerCoordinate": coordinate, "byteLength": size,
                                  "sha256": digest, "artifacts": []}
        return RpfArchive.from_path(path, crypto=crypto)

    def freeze(container: str, coordinate: str, exact: bytes, format_id: str):
        if len(exact) > MAX_SOURCE_BYTES:
            raise ValueError("Route source exceeds artifact bound")
        item = {"sourceCoordinate": coordinate, "byteLength": len(exact), "sha256": sha(exact),
                "formatId": format_id, "formatVersion": "1"}
        relative = _frozen_relative(coordinate)
        if output is not None:
            target = output / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as stream:
                stream.write(exact)
        containers[container]["artifacts"].append(dict(item, frozenRelativePath=relative))
        artifacts.append(item)
        return item

    ynds = []
    archive = outer("update/update.rpf")
    try:
        paths_bytes = actor._read_entry(archive, "x64/levels/gta5/paths.rpf")
        if len(paths_bytes) != MANIFEST["pathsByteLength"] or sha(paths_bytes) != MANIFEST["pathsSha256"]:
            raise ValueError("Shipped paths RPF differs from the admitted source")
        freeze("update/update.rpf", MANIFEST["pathsCoordinate"], paths_bytes, RPF_FORMAT)
        nested = RpfArchive.from_bytes(paths_bytes, name="paths.rpf", crypto=crypto)
        try:
            entries = sorted((e for e in nested.iter_entries() if isinstance(e, RpfFileEntry)
                              and re.fullmatch(MANIFEST["memberPattern"], e.path)), key=lambda e: e.path)
            for entry in entries:
                exact = nested.read_entry_standalone(entry)
                coordinate = MANIFEST["pathsCoordinate"] + "!/" + entry.path
                item = freeze("update/update.rpf", coordinate, exact, YND_FORMAT)
                ynds.append({"sourceCoordinate": coordinate, "nodes": _parse_ynd(exact)})
        finally:
            nested.close()
    finally:
        archive.close()
    archive = outer("x64b.rpf")
    try:
        language = actor._read_entry(archive, "data/lang/american_rel.rpf")
        freeze("x64b.rpf", MANIFEST["languageCoordinate"], language, RPF_FORMAT)
        nested = RpfArchive.from_bytes(language, name="american_rel.rpf", crypto=crypto)
        try:
            gxt = actor._read_entry(nested, "global.gxt2")
        finally:
            nested.close()
        if sha(gxt) != MANIFEST["gxtSha256"]:
            raise ValueError("Base American GXT2 differs from the admitted source")
        freeze("x64b.rpf", MANIFEST["gxtCoordinate"], gxt, GXT_FORMAT)
    finally:
        archive.close()
    counts = _counts(ynds, gxt)
    if counts != MANIFEST["exactBuildClosure"]:
        raise ValueError(f"Route family closure changed: {counts}")
    decoder = {"methodId": DECODER_METHOD, "exactVersion": lock["exactVersion"],
               "artifactSha256": lock["windowsX64WheelSha256"]}
    index = {"schemaId": "grid.gta-v.route-corpus-index", "schemaVersion": 1,
             "decoder": decoder, "locale": "en-US", "scope": MANIFEST["scope"],
             "artifacts": sorted(artifacts, key=lambda v: v["sourceCoordinate"]), "ynds": ynds,
             "gxtCoordinate": MANIFEST["gxtCoordinate"], "counts": counts}
    index_bytes = canonical(index) + b"\n"
    for container in containers.values():
        container["artifacts"].sort(key=lambda v: v["sourceCoordinate"])
    receipt = {"schemaVersion": 1, "gameId": MANIFEST["gameId"],
               "gameVersionNamespace": "valve.steam.app.3240220.build-id", "gameVersion": platform["buildId"],
               "observedAtUtc": observed, "platformObservation": platform,
               "sourceFamilyManifest": {"manifestId": MANIFEST["manifestId"], "schemaVersion": 1,
                                        "documentSha256": sha(canonical(MANIFEST))},
               "acquisitionTool": {"toolId": "fivefury.rpf+ynd+gxt2-read", "exactVersion": lock["exactVersion"],
                                   "artifactSha256": lock["windowsX64WheelSha256"], "sourceRevision": lock["sourceRevision"],
                                   "methodId": "grid.gta-v-enhanced.route-acquisition", "methodVersion": "1", "receiptSchemaVersion": 1},
               "containers": sorted(containers.values(), key=lambda v: v["containerCoordinate"]),
               "routeCorpusIndex": {"fileName": INDEX_NAME, "schemaId": index["schemaId"], "schemaVersion": 1,
                                    "decoderMethodId": decoder["methodId"], "decoderExactVersion": decoder["exactVersion"],
                                    "decoderArtifactSha256": decoder["artifactSha256"], "documentByteLength": len(index_bytes),
                                    "documentSha256": sha(index_bytes)}}
    return receipt, index


def acquire(args, lock):
    output = Path(args.output).resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Route output must be absent or empty")
    output.mkdir(parents=True, exist_ok=True)
    receipt, index = _build_snapshot(Path(args.game_root).resolve(strict=True), lock,
                                     actor._validate_observed(args.observed_at_utc), output)
    receipt["receiptDocumentSha256"] = sha(canonical(receipt))
    (output / INDEX_NAME).write_bytes(canonical(index) + b"\n")
    (output / RECEIPT_NAME).write_bytes(canonical(receipt) + b"\n")


def verify(args, lock):
    receipt_path = Path(args.receipt).resolve(strict=True)
    root = receipt_path.parent
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    digest = receipt.pop("receiptDocumentSha256", None)
    if digest != sha(canonical(receipt)):
        raise ValueError("Route receipt digest mismatch")
    actual, index = _build_snapshot(Path(args.game_root).resolve(strict=True), lock,
                                    actor._validate_observed(receipt["observedAtUtc"]), None)
    if actual != receipt or (root / INDEX_NAME).read_bytes() != canonical(index) + b"\n":
        raise ValueError("Route bundle differs from the exact shipped source projection")
    for container in receipt["containers"]:
        for item in container["artifacts"]:
            relative = item["frozenRelativePath"]
            if relative != _frozen_relative(item["sourceCoordinate"]):
                raise ValueError("Noncanonical route frozen path")
            exact = (root / relative).read_bytes()
            if len(exact) != item["byteLength"] or sha(exact) != item["sha256"]:
                raise ValueError("Frozen route source changed")


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("acquire", "verify"):
        command = sub.add_parser(name)
        command.add_argument("--game-root", required=True)
        command.add_argument("--fivefury-wheel", required=True)
        if name == "acquire":
            command.add_argument("--output", required=True)
            command.add_argument("--observed-at-utc", required=True)
        else:
            command.add_argument("--receipt", required=True)
    args = parser.parse_args()
    lock = actor._load_lock(Path(args.fivefury_wheel).resolve(strict=True))
    (acquire if args.command == "acquire" else verify)(args, lock)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
