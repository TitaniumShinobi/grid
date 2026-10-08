import importlib.util
import sys
import unittest
from pathlib import Path

SOURCE = Path(__file__).parents[1] / "catalog/gta_v_enhanced_presentation_acquire.py"
spec = importlib.util.spec_from_file_location("presentation_acquire", SOURCE)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)


class PresentationAcquisitionTests(unittest.TestCase):
    def test_manifest_is_complete_and_pinned(self):
        manifest = module.load_manifest()
        self.assertEqual(len(manifest["references"]), 6)
        self.assertEqual(len(manifest["languageContainer"]["members"]), 2)
        self.assertTrue(all(len(row["sha256"]) == 64 for row in manifest["references"]))

    def test_duplicate_json_is_rejected(self):
        with self.assertRaises(ValueError):
            module.strict_json(b'{"schemaVersion":1,"schemaVersion":2}')

    def test_paths_are_canonical_relative(self):
        for value in ["../evil", "/root", "C:/root", "a\\b", "a//b", "a/./b"]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                module.relative_path(value)

    def test_changed_source_bytes_are_rejected(self):
        with self.assertRaises(ValueError):
            module.checked(b"changed", module.sha(b"approved"), "fixture")

    def test_read_only_pin_coordinates(self):
        for row in module.load_manifest()["references"]:
            coordinate = module.reference_coordinate(row)
            self.assertTrue(coordinate.startswith("https://"))
            self.assertIn(row["revision"], coordinate)


if __name__ == "__main__":
    unittest.main()
