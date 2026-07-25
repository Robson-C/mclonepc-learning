from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from build_release_manifest import build_manifest  # noqa: E402
from release_contract import (  # noqa: E402
    ContractError,
    validate_manifest,
    verify_artifacts,
)


class ReleaseContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_directory.name)
        self.windows = self.root / "MClonePC-Windows.zip"
        self.android = self.root / "MClonePC-Android.apk"
        self.windows.write_bytes(b"windows-artifact")
        self.android.write_bytes(b"android-artifact")

    def tearDown(self) -> None:
        self.temp_directory.cleanup()

    def build(self) -> dict:
        return build_manifest(
            version="0.1.0",
            version_code=100,
            repository="Robson-C/mclonepc-learning",
            artifacts=[
                ("windows-x64", self.windows),
                ("android", self.android),
            ],
            channel="development",
            release_notes="Contrato inicial",
        )

    def test_manifest_and_artifacts_are_valid(self) -> None:
        manifest = self.build()
        validate_manifest(manifest)
        self.assertEqual([], verify_artifacts(manifest, self.root))

    def test_tampering_is_detected(self) -> None:
        manifest = self.build()
        self.android.write_bytes(b"changed")
        errors = verify_artifacts(manifest, self.root)
        self.assertTrue(any("Android.apk" in error for error in errors))

    def test_duplicate_platform_is_rejected(self) -> None:
        manifest = self.build()
        manifest["artifacts"][1]["platform"] = "windows-x64"
        with self.assertRaisesRegex(ContractError, "plataforma duplicada"):
            validate_manifest(manifest)

    def test_non_https_download_is_rejected(self) -> None:
        manifest = self.build()
        manifest["artifacts"][0]["url"] = "http://example.invalid/file.zip"
        with self.assertRaisesRegex(ContractError, "URL HTTPS inválida"):
            validate_manifest(manifest)

    def test_unsafe_filename_is_rejected(self) -> None:
        manifest = self.build()
        manifest["artifacts"][0]["filename"] = "../outside.zip"
        with self.assertRaisesRegex(ContractError, "filename inseguro"):
            validate_manifest(manifest)


if __name__ == "__main__":
    unittest.main()

