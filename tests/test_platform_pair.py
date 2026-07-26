from __future__ import annotations

import json
import sys
import tempfile
import unittest
import zipfile
import struct
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from verify_platform_pair import (  # noqa: E402
    PlatformPairError,
    sha256_bytes,
    validate_pair,
    verify_artifacts,
)


class PlatformPairTests(unittest.TestCase):
    @staticmethod
    def build_car(entries: list[tuple[str, bytes]]) -> bytes:
        index_size = 16
        for name, _ in entries:
            encoded = name.encode("utf-8")
            index_size += 12 + ((len(encoded) + 1 + 3) & ~3)
        offsets: list[int] = []
        blocks: list[bytes] = []
        offset = index_size
        for _, payload in entries:
            padding = (-len(payload)) % 4
            block_size = 4 + len(payload) + padding
            block = (
                struct.pack("<III", 2, block_size, len(payload))
                + payload
                + b"\0" * padding
            )
            offsets.append(offset)
            blocks.append(block)
            offset += len(block)
        result = bytearray(b"rac\x01" + struct.pack("<III", 1, 0, len(entries)))
        for (name, _), entry_offset in zip(entries, offsets, strict=True):
            encoded = name.encode("utf-8")
            result.extend(struct.pack("<III", 1, entry_offset, len(encoded)))
            result.extend(encoded)
            result.append(0)
            while len(result) % 4:
                result.append(0)
        for block in blocks:
            result.extend(block)
        result.extend(b"\xff\xff\xff\xff\0\0\0\0")
        return bytes(result)

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.windows_car_bytes = self.build_car(
            [("main.lu", b"windows-main"), ("obj.GameClass.lu", b"shared")]
        )
        self.android_car_bytes = self.build_car(
            [("main.lu", b"android-main"), ("obj.GameClass.lu", b"shared")]
        )
        self.windows_sha256 = sha256_bytes(self.windows_car_bytes)
        self.android_sha256 = sha256_bytes(self.android_car_bytes)
        self.windows_car = self.root / "resource.car"
        self.windows_car.write_bytes(self.windows_car_bytes)
        self.android_apk = self.root / "MClonePC-Android.apk"
        with zipfile.ZipFile(self.android_apk, "w") as apk:
            apk.writestr("assets/resource.car", self.android_car_bytes)
        self.pair = {
            "schema_version": 1,
            "version": "9.3.3",
            "version_code": 90303,
            "core": {
                "windows_reference_car_sha256": self.windows_sha256,
                "allowed_platform_differences": ["main.lu"],
            },
            "save": {
                "contract_id": "mclonepc-game-cloud-v1",
                "schema_version": 1,
                "remote_file_name": "mclonepc-game-cloud-v1.json",
            },
            "platforms": {
                "windows": {
                    "project": "MClonePC Windows",
                    "resource_car_sha256": self.windows_sha256,
                    "save_contract_id": "mclonepc-game-cloud-v1",
                },
                "android": {
                    "project": "MClonePC Android",
                    "resource_car_sha256": self.android_sha256,
                    "save_contract_id": "mclonepc-game-cloud-v1",
                },
            },
        }

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_linked_artifacts_are_accepted(self) -> None:
        result = verify_artifacts(
            self.pair, self.windows_car, self.android_apk
        )
        self.assertEqual(
            self.android_sha256, result["android_resource_car_sha256"]
        )
        self.assertEqual("1", result["common_entries_verified"])
        self.assertEqual("main.lu", result["platform_differences"])
        self.assertEqual(
            "mclonepc-game-cloud-v1", result["save_contract_id"]
        )

    def test_android_core_divergence_is_rejected(self) -> None:
        with zipfile.ZipFile(self.android_apk, "w") as apk:
            apk.writestr(
                "assets/resource.car",
                self.build_car(
                    [
                        ("main.lu", b"android-main"),
                        ("obj.GameClass.lu", b"different"),
                    ]
                ),
            )
        self.pair["platforms"]["android"]["resource_car_sha256"] = sha256_bytes(
            self.build_car(
                [
                    ("main.lu", b"android-main"),
                    ("obj.GameClass.lu", b"different"),
                ]
            )
        )
        with self.assertRaisesRegex(
            PlatformPairError, "módulos comuns divergentes"
        ):
            verify_artifacts(self.pair, self.windows_car, self.android_apk)

    def test_platform_contract_divergence_is_rejected(self) -> None:
        self.pair["platforms"]["android"]["save_contract_id"] = "other"
        with self.assertRaisesRegex(PlatformPairError, "save.*diverge"):
            validate_pair(self.pair)

    def test_pair_can_be_serialized_for_private_builds(self) -> None:
        path = self.root / "platform-pair.json"
        path.write_text(
            json.dumps(self.pair, indent=2) + "\n", encoding="utf-8"
        )
        validate_pair(json.loads(path.read_text(encoding="utf-8")))


if __name__ == "__main__":
    unittest.main()
