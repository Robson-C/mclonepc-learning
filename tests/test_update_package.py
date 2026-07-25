from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from build_update_package import (  # noqa: E402
    PackageError,
    build_package_manifest,
    write_package,
)


class UpdatePackageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_directory.name)
        self.payload = self.root / "payload"
        self.payload.mkdir()
        (self.payload / "MClonePC.version.json").write_text(
            '{"version":"9.2.1"}\n', encoding="utf-8"
        )
        nested = self.payload / "Resources" / "Mods"
        nested.mkdir(parents=True)
        (nested / "README.txt").write_text("mod local\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.temp_directory.cleanup()

    def test_package_is_deterministic_and_complete(self) -> None:
        manifest = build_package_manifest(
            payload_directory=self.payload,
            version="9.2.1",
            version_code=90201,
            supported_from_version_codes=[90200],
        )
        first = self.root / "first.zip"
        second = self.root / "second.zip"
        write_package(
            payload_directory=self.payload, output=first, manifest=manifest
        )
        write_package(
            payload_directory=self.payload, output=second, manifest=manifest
        )
        self.assertEqual(
            hashlib.sha256(first.read_bytes()).hexdigest(),
            hashlib.sha256(second.read_bytes()).hexdigest(),
        )

        with zipfile.ZipFile(first) as archive:
            self.assertEqual(
                [
                    "package.json",
                    "payload/MClonePC.version.json",
                    "payload/Resources/Mods/README.txt",
                ],
                archive.namelist(),
            )
            stored = json.loads(archive.read("package.json"))
            self.assertEqual(90201, stored["version_code"])
            self.assertEqual(2, len(stored["files"]))

    def test_empty_payload_is_rejected(self) -> None:
        empty = self.root / "empty"
        empty.mkdir()
        with self.assertRaisesRegex(PackageError, "não contém arquivos"):
            build_package_manifest(
                payload_directory=empty,
                version="9.2.1",
                version_code=90201,
                supported_from_version_codes=[90200],
            )

    def test_equal_or_newer_source_is_rejected(self) -> None:
        with self.assertRaisesRegex(PackageError, "menores que o destino"):
            build_package_manifest(
                payload_directory=self.payload,
                version="9.2.1",
                version_code=90201,
                supported_from_version_codes=[90201],
            )


if __name__ == "__main__":
    unittest.main()

