"""Version-compatibility checks for the preproduction GTA acquisition boundary."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from copy import deepcopy
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "catalog" / "gta_v_enhanced_acquire.py"
SPEC = importlib.util.spec_from_file_location("grid_gta_v_enhanced_acquire", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Could not load GTA acquisition module")
ACQUISITION = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ACQUISITION)

ACTOR_SCRIPT = Path(__file__).parents[1] / "catalog" / "gta_v_enhanced_actor_acquire.py"
ACTOR_SPEC = importlib.util.spec_from_file_location(
    "grid_gta_v_enhanced_actor_acquire", ACTOR_SCRIPT
)
if ACTOR_SPEC is None or ACTOR_SPEC.loader is None:
    raise RuntimeError("Could not load GTA Actor acquisition module")
ACTOR_ACQUISITION = importlib.util.module_from_spec(ACTOR_SPEC)
ACTOR_SPEC.loader.exec_module(ACTOR_ACQUISITION)
sys.modules["gta_v_enhanced_actor_acquire"] = ACTOR_ACQUISITION

SPATIAL_SCRIPT = (
    Path(__file__).parents[1] / "catalog" / "gta_v_enhanced_spatial_acquire.py"
)
SPATIAL_SPEC = importlib.util.spec_from_file_location(
    "grid_gta_v_enhanced_spatial_acquire", SPATIAL_SCRIPT
)
if SPATIAL_SPEC is None or SPATIAL_SPEC.loader is None:
    raise RuntimeError("Could not load GTA spatial acquisition module")
SPATIAL_ACQUISITION = importlib.util.module_from_spec(SPATIAL_SPEC)
SPATIAL_SPEC.loader.exec_module(SPATIAL_ACQUISITION)


class GtaVEnhancedAcquisitionCompatibilityTests(unittest.TestCase):
    def test_fixed_member_set_includes_exact_ambient_ped_source(self) -> None:
        self.assertEqual(
            (
                "common/data/ai/ambientpedmodelsets.meta",
                "common/data/gen9_exclusive_assets_peds.meta",
                "common/data/levels/gta5/popzone.ipl",
                "x64/data/cdimages/scaleform_generic.rpf!/hud.gfx",
                "x64/patch/data/lang/american_rel.rpf",
                "x64/patch/data/lang/american_rel.rpf!/global.gxt2",
            ),
            ACQUISITION.FIXED_MEMBERS["update/update.rpf"],
        )

    def test_source_family_manifest_declares_exact_current_closure(self) -> None:
        manifest = ACQUISITION.SOURCE_FAMILY_MANIFEST
        self.assertEqual(
            "a477091b010d4a80958a77a35247a520cf98b8df4d8b8c27c76c979e8be03338",
            ACQUISITION.SOURCE_FAMILY_MANIFEST_SHA256,
        )
        self.assertEqual(2, manifest["schemaVersion"])
        self.assertEqual("25261616", manifest["steamBuildId"])
        self.assertEqual(4, len(manifest["containers"]))
        self.assertEqual(10, sum(
            len(container.get("members", ())) for container in manifest["containers"]
        ))
        dynamics = [container["dynamicMembers"] for container in manifest["containers"]
                    if "dynamicMembers" in container]
        self.assertEqual([40, 1052], sorted(value["exactCount"] for value in dynamics))
        self.assertEqual(1102, 10 + sum(value["exactCount"] for value in dynamics))
        family_statuses = {
            family["sourceFamilyId"]: family["status"]
            for family in manifest["sourceFamilies"]
        }
        self.assertEqual("diagnostic", family_statuses["rockstar.gta-v.enhanced.hud-gfx"])
        self.assertEqual(
            "indexed-unconsumed",
            family_statuses["rockstar.gta-v.enhanced.patch-american-localization"],
        )
        self.assertEqual(
            "supported",
            family_statuses["rockstar.gta-v.enhanced.online-activity-registry"],
        )

    def test_source_family_manifest_rejects_wrong_but_valid_tuple_and_order(self) -> None:
        cases = []
        wrong_format = deepcopy(ACQUISITION.SOURCE_FAMILY_MANIFEST)
        wrong_format["containers"][0]["members"][0]["formatId"] = (
            "rockstar.gta-v.mapzones.cmapzonescontainer-xml"
        )
        cases.append(wrong_format)
        wrong_family = deepcopy(ACQUISITION.SOURCE_FAMILY_MANIFEST)
        wrong_family["containers"][0]["members"][0]["sourceFamilyId"] = (
            "rockstar.gta-v.enhanced.mapzones"
        )
        cases.append(wrong_family)
        wrong_version = deepcopy(ACQUISITION.SOURCE_FAMILY_MANIFEST)
        wrong_version["containers"][0]["members"][0]["exactFormatVersion"] = "999"
        cases.append(wrong_version)
        wrong_kind = deepcopy(ACQUISITION.SOURCE_FAMILY_MANIFEST)
        next(
            family for family in wrong_kind["sourceFamilies"]
            if family["sourceFamilyId"] == "rockstar.gta-v.enhanced.weapons-meta"
        )["knowledgeKinds"] = ["Actor"]
        cases.append(wrong_kind)
        wrong_status = deepcopy(ACQUISITION.SOURCE_FAMILY_MANIFEST)
        family = next(
            family for family in wrong_status["sourceFamilies"]
            if family["sourceFamilyId"] == "rockstar.gta-v.enhanced.weapons-meta"
        )
        family["status"] = "indexed-unconsumed"
        family["reasonCode"] = "tampered-policy"
        cases.append(wrong_status)
        reordered = deepcopy(ACQUISITION.SOURCE_FAMILY_MANIFEST)
        reordered["containers"].reverse()
        cases.append(reordered)
        wrong_count = deepcopy(ACQUISITION.SOURCE_FAMILY_MANIFEST)
        next(
            container for container in wrong_count["containers"]
            if container["memberMode"] == "dynamic-prefix"
        )["dynamicMembers"]["exactCount"] = 1051
        cases.append(wrong_count)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "manifest.json"
            for case in cases:
                with self.subTest(case=case):
                    path.write_text(json.dumps(case), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        ACQUISITION._load_source_family_manifest(path)

    def test_receipt_closure_is_exact_ordered_and_version_compatible(self) -> None:
        def current_receipt() -> dict:
            containers = []
            for declaration in ACQUISITION.SOURCE_FAMILY_MANIFEST["containers"]:
                coordinate = declaration["containerCoordinate"]
                paths = [member["memberPath"] for member in declaration.get("members", ())]
                if "dynamicMembers" in declaration:
                    dynamic = declaration["dynamicMembers"]
                    paths.extend(
                        f"{dynamic['prefix']}fixture-{index:04d}{dynamic['suffix']}"
                        for index in range(dynamic["exactCount"])
                    )
                paths.sort()
                containers.append({
                    "containerCoordinate": coordinate,
                    "members": [
                        {
                            "memberPath": path,
                            "memberCoordinate": f"{coordinate}!/{path}",
                        }
                        for path in paths
                    ],
                })
            return {
                "sourceFamilyManifest": {
                    "manifestId": ACQUISITION.SOURCE_FAMILY_MANIFEST["manifestId"],
                    "schemaVersion": 2,
                    "documentSha256": ACQUISITION.SOURCE_FAMILY_MANIFEST_SHA256,
                },
                "containers": containers,
            }

        valid = current_receipt()
        ACQUISITION._validate_receipt_closure(valid)

        legacy = current_receipt()
        legacy["sourceFamilyManifest"] = {
            "manifestId": "grid.gta-v-enhanced.source-families",
            "schemaVersion": 1,
            "documentSha256": ACQUISITION.LEGACY_SOURCE_FAMILY_MANIFEST_SHA256,
        }
        common = next(
            container for container in legacy["containers"]
            if container["containerCoordinate"] == "common.rpf"
        )
        common["members"] = [
            member for member in common["members"]
            if not member["memberPath"].startswith("data/ugc/")
        ]
        ACQUISITION._validate_receipt_closure(legacy)

        legacy.pop("sourceFamilyManifest")
        update = next(
            container for container in legacy["containers"]
            if container["containerCoordinate"] == "update/update.rpf"
        )
        update["members"] = [
            member for member in update["members"]
            if not member["memberPath"].endswith("hud.gfx")
        ]
        ACQUISITION._validate_receipt_closure(legacy)

        mutations = []
        missing = deepcopy(valid)
        missing["containers"][0]["members"].pop()
        mutations.append(missing)
        extra = deepcopy(valid)
        extra["containers"][0]["members"].append({
            "memberPath": "extra.bin",
            "memberCoordinate": "common.rpf!/extra.bin",
        })
        mutations.append(extra)
        duplicate = deepcopy(valid)
        duplicate["containers"][0]["members"].append(
            deepcopy(duplicate["containers"][0]["members"][0])
        )
        mutations.append(duplicate)
        reordered = deepcopy(valid)
        reordered["containers"].reverse()
        mutations.append(reordered)
        wrong_coordinate = deepcopy(valid)
        wrong_coordinate["containers"][0]["members"][0]["memberCoordinate"] = "common.rpf!/wrong.meta"
        mutations.append(wrong_coordinate)
        wrong_binding = deepcopy(valid)
        wrong_binding["sourceFamilyManifest"]["documentSha256"] = "0" * 64
        mutations.append(wrong_binding)
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                with self.assertRaises(ValueError):
                    ACQUISITION._validate_receipt_closure(mutation)

    def test_v1_replay_keeps_whole_manifest_semantics_without_v2_reinterpretation(self) -> None:
        exact_bytes = (
            b'"WrongState"\r\n{\r\n'
            b'\t"appid"\t\t"3240220"\r\n'
            b'\t"buildid"\t\t"25261616"\r\n'
            b'\t"installdir"\t\t"Grand Theft Auto V Enhanced"\r\n}\r\n'
        )
        with tempfile.TemporaryDirectory() as directory:
            steamapps = Path(directory) / "steamapps"
            game_root = steamapps / "common" / "Grand Theft Auto V Enhanced"
            game_root.mkdir(parents=True)
            manifest = steamapps / "appmanifest_3240220.acf"
            manifest.write_bytes(exact_bytes)
            observation = {
                "provider": "valve.steam",
                "appId": "3240220",
                "buildId": "25261616",
                "manifestCoordinate": "steamapps/appmanifest_3240220.acf",
                "manifestByteLength": len(exact_bytes),
                "manifestSha256": hashlib.sha256(exact_bytes).hexdigest(),
                "appIdFieldPath": "/AppState/appid",
                "buildIdFieldPath": "/AppState/buildid",
                "installDirFieldPath": "/AppState/installdir",
            }
            ACQUISITION._verify_legacy_platform_observation(game_root, observation)
            with self.assertRaises(ValueError):
                ACQUISITION._parse_app_state_identity(exact_bytes.decode("utf-8"))

    def test_v2_parser_requires_exact_direct_app_state_members(self) -> None:
        valid = (
            '"AppState"\n{\n'
            '\t"appid" "3240220"\n'
            '\t"buildid" "25261616"\n'
            '\t"installdir" "Grand Theft Auto V Enhanced"\n'
            '\t"LastPlayed" "volatile"\n}\n'
        )
        self.assertEqual(
            ("3240220", "25261616", "Grand Theft Auto V Enhanced"),
            ACQUISITION._parse_app_state_identity(valid),
        )
        for invalid in (
            valid.replace('"AppState"', '"WrongState"'),
            valid.replace('"appid"', '"AppId"'),
            valid.replace('\t"buildid" "25261616"\n', ""),
            valid.replace(
                '\t"appid" "3240220"\n',
                '\t"Nested"\n\t{\n\t\t"appid" "3240220"\n\t}\n',
            ),
        ):
            with self.subTest(invalid=invalid):
                with self.assertRaises(ValueError):
                    ACQUISITION._parse_app_state_identity(invalid)


class GtaVEnhancedActorAcquisitionTests(unittest.TestCase):
    @staticmethod
    def _mount_fixture() -> tuple[list[str], list[str], list[str]]:
        graph = ACTOR_ACQUISITION.MANIFEST["mountGraph"]
        ped_packs = list(graph["pedMetadataPacks"])
        physical = list(ped_packs)
        physical.append("mp2025_01_g9ec")
        physical.extend(f"unused_{index:02d}" for index in range(66))
        dlcpacks = [f"dlcpacks:/{name}/" for name in physical]
        dlcpacks.append("dlcpacks:/patchbvh00/")
        dlcpacks.append("dlcpacks:/mp2025_01_g9ec/")
        platform = [f"platform:/dlcPacks/base_{index:02d}/" for index in range(10)]
        paths = platform + dlcpacks
        if len(paths) != 104 or len(physical) != 92:
            raise AssertionError("Invalid synthetic Actor mount-closure fixture")
        return paths, physical, ped_packs

    def test_actor_manifest_freezes_exact_build_source_closure(self) -> None:
        manifest = ACTOR_ACQUISITION.MANIFEST
        graph = manifest["mountGraph"]
        self.assertEqual(1, manifest["schemaVersion"])
        self.assertEqual("25261616", manifest["steamBuildId"])
        self.assertEqual(683, manifest["exactResidentRecordCount"])
        self.assertEqual(435, manifest["exactDlcRecordCount"])
        self.assertEqual(
            "63fe8c6a97c011cb70a62e0f88e22bcb1e42fb70fcac86dbb7d450aebc3c50ce",
            ACTOR_ACQUISITION.MANIFEST_SHA256,
        )
        self.assertEqual((104, 103, 10, 94, 92, 25), tuple(
            graph[name] for name in (
                "exactOccurrenceCount", "exactUniqueCasefoldedPathCount",
                "exactPlatformOccurrenceCount", "exactDlcPacksOccurrenceCount",
                "exactPhysicalDlcPacksCount", "exactPedMetadataRegistrationCount",
            )
        ))
        self.assertEqual(
            ["dlcpacks:/mp2025_01_g9ec/"], graph["duplicateNormalizedMountPaths"]
        )
        self.assertEqual(
            ["dlcpacks:/patchbvh00/"], graph["declaredPhysicalContainerExceptions"]
        )

    def test_registration_v4_adds_actor_manifest_without_reinterpreting_v3(self) -> None:
        catalog = Path(__file__).parents[1] / "catalog"
        previous = json.loads(
            (catalog / "gta_v_enhanced_registration_sources.v3.json").read_text(
                encoding="utf-8"
            )
        )
        current = json.loads(
            (catalog / "gta_v_enhanced_registration_sources.v4.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(4, current["schemaVersion"])
        self.assertEqual(
            previous["localSourceFamilyManifest"], current["localSourceFamilyManifest"]
        )
        self.assertEqual(previous["referenceSources"], current["referenceSources"])
        self.assertEqual(
            {
                "manifestId": "grid.gta-v-enhanced.actor-source-families",
                "schemaVersion": 1,
                "documentSha256": ACTOR_ACQUISITION.MANIFEST_SHA256,
            },
            current["actorSourceFamilyManifest"],
        )

    def test_actor_mount_closure_is_exact_and_fail_closed(self) -> None:
        paths, physical, ped_packs = self._mount_fixture()
        ACTOR_ACQUISITION._validate_mount_closure(paths, physical, ped_packs)
        mutations = []
        missing = deepcopy(paths)
        missing.pop()
        mutations.append((missing, physical, ped_packs))
        extra = deepcopy(paths)
        extra.append("dlcpacks:/extra/")
        mutations.append((extra, physical, ped_packs))
        wrong_duplicate = deepcopy(paths)
        wrong_duplicate[-1] = "dlcpacks:/other_duplicate/"
        mutations.append((wrong_duplicate, physical, ped_packs))
        wrong_exception = deepcopy(paths)
        wrong_exception[wrong_exception.index("dlcpacks:/patchbvh00/")] = (
            "dlcpacks:/different_missing/"
        )
        mutations.append((wrong_exception, physical, ped_packs))
        missing_physical = deepcopy(physical)
        missing_physical.pop()
        mutations.append((paths, missing_physical, ped_packs))
        wrong_ped_pack = deepcopy(ped_packs)
        wrong_ped_pack[-1] = "not_authorized"
        mutations.append((paths, physical, wrong_ped_pack))
        for values in mutations:
            with self.subTest(values=values):
                with self.assertRaises(ValueError):
                    ACTOR_ACQUISITION._validate_mount_closure(*values)

    def test_actor_xml_parsers_preserve_exact_fields_and_locators(self) -> None:
        dlclist = (
            b'<SMandatoryPacksData><Paths>'
            b'<Item>dlcpacks:/first/</Item>'
            b'<item>dlcpacks:/second/</item>'
            b'<Item platform="ps5|xbsx">dlcpacks:/third/</Item>'
            b'</Paths></SMandatoryPacksData>'
        )
        occurrences = ACTOR_ACQUISITION._parse_dlclist_occurrences(dlclist)
        self.assertEqual([0, 1, 2], [item["ordinal"] for item in occurrences])
        self.assertEqual("dlcpacks:/second/", occurrences[1]["rawMountPath"])
        self.assertTrue(occurrences[1]["fieldLocator"].endswith("/Paths[1]/*[2]"))
        self.assertTrue(occurrences[2]["fieldLocator"].endswith("/Paths[1]/*[3]"))
        setup = (
            b'<?xml version="1.0" encoding="UTF-8"?>'
            b'<SSetupData><deviceName>dlc_fixture</deviceName>'
            b'<datFile>content.xml</datFile></SSetupData>'
        )
        self.assertEqual(
            ("dlc_fixture", "content.xml"),
            ACTOR_ACQUISITION._parse_setup(setup),
        )
        content = (
            b'<CDataFileMgr__ContentsOfDataFileXml><dataFiles><Item>'
            b'<filename>dlc_fixture:/common/data/peds.meta</filename>'
            b'<fileType>PED_METADATA_FILE</fileType>'
            b'</Item></dataFiles></CDataFileMgr__ContentsOfDataFileXml>'
        )
        self.assertEqual(
            [(0, 0, "dlc_fixture:/common/data/peds.meta", "PED_METADATA_FILE")],
            ACTOR_ACQUISITION._parse_content(content),
        )
        coordinate = "update/x64/dlcpacks/fixture/dlc.rpf!/common/data/peds.meta"
        peds = (
            b'\xef\xbb\xbf<?xml version="1.0" encoding="UTF-8"?>'
            b'<CPedModelInfo__InitDataList><InitDatas><Item>'
            b'<Name>MODEL_EXACT</Name><Pedtype>CIVMALE</Pedtype>'
            b'</Item></InitDatas></CPedModelInfo__InitDataList>'
        )
        records = ACTOR_ACQUISITION._parse_dlc_ped_records(peds, coordinate)
        self.assertEqual(1, len(records))
        self.assertEqual("MODEL_EXACT", records[0]["nameExact"])
        self.assertEqual("CIVMALE", records[0]["pedtypeExact"])
        self.assertIn("Item[1]/Name[1]", records[0]["nameFieldLocator"])

    def test_actor_xml_parsers_reject_ambiguous_or_unsafe_input(self) -> None:
        cases = (
            b'<SSetupData><deviceName>x</deviceName><deviceName>y</deviceName></SSetupData>',
            b'<!DOCTYPE SSetupData><SSetupData><deviceName>x</deviceName></SSetupData>',
        )
        for exact in cases:
            with self.subTest(exact=exact):
                with self.assertRaises(ValueError):
                    ACTOR_ACQUISITION._parse_setup(exact)
        duplicate_name = (
            b'<CPedModelInfo__InitDataList><InitDatas><Item>'
            b'<Name>A</Name><Name>B</Name><Pedtype>CIVMALE</Pedtype>'
            b'</Item></InitDatas></CPedModelInfo__InitDataList>'
        )
        with self.assertRaises(ValueError):
            ACTOR_ACQUISITION._parse_dlc_ped_records(duplicate_name, "fixture!/peds.meta")

    def test_actor_sidecar_artifact_tuple_is_content_bound(self) -> None:
        exact = b"exact-rockstar-bytes"
        artifact = ACTOR_ACQUISITION._artifact_tuple(
            "update/update.rpf!/x64/data/peds.ymt", exact,
            "rockstar.gta-v.ped-model-init-data-list-pso", "1",
        )
        self.assertEqual(len(exact), artifact["byteLength"])
        self.assertEqual(hashlib.sha256(exact).hexdigest(), artifact["sha256"])
        self.assertNotIn("artifactId", artifact)

    def test_actor_receipt_binding_rejects_sidecar_or_decoder_tampering(self) -> None:
        index = {
            "schemaId": "grid.gta-v.actor-corpus-index",
            "schemaVersion": 1,
            "decoder": {
                "methodId": "fivefury.ymt.ped-metadata",
                "exactVersion": "0.5.1",
                "artifactSha256": "a" * 64,
            },
            "resident": {"records": []},
            "dlcPacks": [],
        }
        exact = ACTOR_ACQUISITION._canonical_bytes(index) + b"\n"
        lock = {"exactVersion": "0.5.1", "windowsX64WheelSha256": "a" * 64}
        binding = {
            "fileName": "actor-corpus-index.v1.json",
            "schemaId": "grid.gta-v.actor-corpus-index",
            "schemaVersion": 1,
            "decoderMethodId": "fivefury.ymt.ped-metadata",
            "decoderExactVersion": "0.5.1",
            "decoderArtifactSha256": "a" * 64,
            "documentByteLength": len(exact),
            "documentSha256": hashlib.sha256(exact).hexdigest(),
        }
        ACTOR_ACQUISITION._validate_index_binding(binding, exact, lock)
        tampered_index = deepcopy(index)
        tampered_index["resident"]["records"] = [{"nameHash": "0x00000001"}]
        tampered_exact = ACTOR_ACQUISITION._canonical_bytes(tampered_index) + b"\n"
        with self.assertRaises(ValueError):
            ACTOR_ACQUISITION._validate_index_binding(binding, tampered_exact, lock)
        wrong_decoder = deepcopy(binding)
        wrong_decoder["decoderMethodId"] = "unverified.decoder"
        with self.assertRaises(ValueError):
            ACTOR_ACQUISITION._validate_index_binding(wrong_decoder, exact, lock)

    def test_spatial_manifest_freezes_exact_dlc_mlo_closure(self) -> None:
        self.assertEqual(1, SPATIAL_ACQUISITION.MANIFEST["schemaVersion"])
        self.assertEqual("25261616", SPATIAL_ACQUISITION.MANIFEST["steamBuildId"])
        self.assertEqual(
            {
                "spatialPackCount": 79,
                "declaredSpatialRpfCount": 1294,
                "nestedRpfResolvedCount": 1056,
                "ytypArtifactCount": 210,
                "ymapArtifactCount": 847,
                "ymfArtifactCount": 478,
                "discoveredYmfArtifactCount": 485,
                "equivalentDuplicateArtifactCount": 7,
                "mloArchetypeCount": 252,
                "mloRoomCount": 1116,
                "mloPortalCount": 1821,
                "mloInstanceCount": 847,
                "ymfRelationshipCount": 36858,
                "unsupportedOutcomeCount": 246,
            },
            SPATIAL_ACQUISITION.MANIFEST["exactBuildClosure"],
        )

    def test_registration_v5_adds_spatial_manifest_without_reinterpreting_v4(self) -> None:
        catalog = Path(__file__).parents[1] / "catalog"
        previous = json.loads((
            catalog / "gta_v_enhanced_registration_sources.v4.json"
        ).read_text(encoding="utf-8"))
        current = json.loads((
            catalog / "gta_v_enhanced_registration_sources.v5.json"
        ).read_text(encoding="utf-8"))
        self.assertEqual(5, current["schemaVersion"])
        for name in (
            "localSourceFamilyManifest", "actorSourceFamilyManifest", "referenceSources"
        ):
            self.assertEqual(previous[name], current[name])
        self.assertEqual(
            {
                "manifestId": "grid.gta-v-enhanced.spatial-source-families",
                "schemaVersion": 1,
                "documentSha256": SPATIAL_ACQUISITION.MANIFEST_SHA256,
            },
            current["spatialSourceFamilyManifest"],
        )

    def test_spatial_decoder_preserves_native_keys_and_authoritative_edges(self) -> None:
        coordinate = "pack/dlc.rpf!/interior.rpf!/fixture.ytyp"
        self.assertEqual(
            "pack/dlc.rpf!/interior.rpf!/fixture.ytyp",
            SPATIAL_ACQUISITION._nested_coordinate(
                "pack/dlc.rpf", "interior.rpf", "fixture.ytyp"
            ),
        )
        self.assertEqual("0x00000001", SPATIAL_ACQUISITION._hash_text(1))
        self.assertEqual("0xFFFFFFFF", SPATIAL_ACQUISITION._hash_text(-1))
        self.assertEqual(
            "x64/levels/gta5/interiors/example.rpf",
            SPATIAL_ACQUISITION._spatial_rpf_member(
                "dlc_fixture",
                "dlc_fixture:/%PLATFORM%/levels/gta5/interiors/example.rpf",
            ),
        )
        self.assertIsNone(SPATIAL_ACQUISITION._spatial_rpf_member(
            "dlc_fixture", "dlc_fixture:/%PLATFORM%/levels/vehicles.rpf"
        ))
        locator = f"rpf7-member:{coordinate}#/archetypes[1]/rooms[1]"
        self.assertTrue(locator.startswith("rpf7-member:"))

    def test_spatial_sidecar_binding_rejects_content_or_decoder_tampering(self) -> None:
        index = {
            "schemaId": "grid.gta-v.spatial-corpus-index",
            "schemaVersion": 1,
            "decoder": {
                "methodId": "fivefury.gta-v.mounted-mlo-spatial",
                "exactVersion": "0.5.1",
                "artifactSha256": "a" * 64,
            },
            "artifacts": [],
            "packs": [],
            "unsupported": [],
        }
        exact = SPATIAL_ACQUISITION._canonical_bytes(index) + b"\n"
        lock = {"exactVersion": "0.5.1", "windowsX64WheelSha256": "a" * 64}
        binding = {
            "fileName": "spatial-corpus-index.v1.json",
            "schemaId": "grid.gta-v.spatial-corpus-index",
            "schemaVersion": 1,
            "decoderMethodId": "fivefury.gta-v.mounted-mlo-spatial",
            "decoderExactVersion": "0.5.1",
            "decoderArtifactSha256": "a" * 64,
            "documentByteLength": len(exact),
            "documentSha256": hashlib.sha256(exact).hexdigest(),
        }
        SPATIAL_ACQUISITION._validate_index_binding(binding, exact, lock)
        changed = deepcopy(index)
        changed["artifacts"] = [{"sha256": "b" * 64}]
        with self.assertRaises(ValueError):
            SPATIAL_ACQUISITION._validate_index_binding(
                binding, SPATIAL_ACQUISITION._canonical_bytes(changed) + b"\n", lock
            )
        wrong = deepcopy(binding)
        wrong["decoderMethodId"] = "unsupported.decoder"
        with self.assertRaises(ValueError):
            SPATIAL_ACQUISITION._validate_index_binding(wrong, exact, lock)

    def test_spatial_equivalent_artifacts_use_unique_mount_precedence(self) -> None:
        def item(coordinate: str, digest: str, format_id: str = "rockstar.gta-v.ymf-pso"):
            return {
                "artifact": {
                    "sourceCoordinate": coordinate,
                    "sha256": digest,
                    "formatId": format_id,
                }
            }

        low = ("ymfs", item("low.ymf", "a" * 64), 2, "low")
        high = ("ymfs", item("high.ymf", "a" * 64), 9, "high")
        other = ("ymfs", item("other.ymf", "b" * 64), 1, "other")
        effective, duplicates = SPATIAL_ACQUISITION._select_effective_semantic(
            [other, low, high]
        )
        self.assertEqual(
            {"high.ymf", "other.ymf"},
            {value[1]["artifact"]["sourceCoordinate"] for value in effective},
        )
        self.assertEqual("low.ymf", duplicates[0]["sourceCoordinate"])
        self.assertEqual("high.ymf", duplicates[0]["detailCode"])
        with self.assertRaises(ValueError):
            SPATIAL_ACQUISITION._select_effective_semantic([low, ("ymfs", item("tie.ymf", "a" * 64), 2, "tie")])
        with self.assertRaises(ValueError):
            SPATIAL_ACQUISITION._select_effective_semantic([
                low, ("ymaps", item("wrong.ymap", "a" * 64, "rockstar.gta-v.ymap-pso"), 3, "wrong")
            ])


if __name__ == "__main__":
    unittest.main()
