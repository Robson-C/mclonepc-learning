from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
import zipfile
from pathlib import Path
from typing import Any


SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
SAVE_CONTRACT_ID = "mclonepc-game-cloud-v1"
REMOTE_FILE_NAME = "mclonepc-game-cloud-v1.json"


class PlatformPairError(ValueError):
    pass


def sha256_bytes(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _u32(data: bytes, offset: int) -> int:
    if offset < 0 or offset + 4 > len(data):
        raise PlatformPairError("resource.car truncado")
    return struct.unpack_from("<I", data, offset)[0]


def parse_car_payloads(data: bytes) -> list[tuple[str, bytes]]:
    if len(data) < 24 or data[:4] != b"rac\x01":
        raise PlatformPairError("assinatura do resource.car é inválida")
    entry_count = _u32(data, 12)
    cursor = 16
    index: list[tuple[str, int]] = []
    for _ in range(entry_count):
        offset = _u32(data, cursor + 4)
        name_length = _u32(data, cursor + 8)
        cursor += 12
        name_end = cursor + name_length
        if name_end >= len(data):
            raise PlatformPairError("índice do resource.car está truncado")
        name = data[cursor:name_end].decode("utf-8")
        cursor = name_end + 1
        while cursor % 4:
            cursor += 1
        index.append((name, offset))

    payloads: list[tuple[str, bytes]] = []
    for name, offset in index:
        if _u32(data, offset) != 2:
            raise PlatformPairError(f"bloco CAR inválido: {name}")
        payload_size = _u32(data, offset + 8)
        start = offset + 12
        end = start + payload_size
        if end > len(data):
            raise PlatformPairError(f"payload CAR truncado: {name}")
        payloads.append((name, data[start:end]))
    return payloads


def validate_pair(pair: dict[str, Any]) -> None:
    if pair.get("schema_version") != 1:
        raise PlatformPairError("schema_version do par deve ser 1")
    if not re.fullmatch(
        r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?",
        str(pair.get("version", "")),
    ):
        raise PlatformPairError("versão do par é inválida")
    if not isinstance(pair.get("version_code"), int) or pair["version_code"] < 1:
        raise PlatformPairError("version_code do par é inválido")

    core = pair.get("core")
    if not isinstance(core, dict):
        raise PlatformPairError("núcleo comum ausente")
    expected_hash = core.get("windows_reference_car_sha256")
    if not isinstance(expected_hash, str) or not SHA256_PATTERN.fullmatch(
        expected_hash
    ):
        raise PlatformPairError("SHA-256 do CAR Windows de referência é inválido")
    allowed = core.get("allowed_platform_differences")
    if (
        not isinstance(allowed, list)
        or any(not isinstance(name, str) or not name for name in allowed)
        or len(set(allowed)) != len(allowed)
    ):
        raise PlatformPairError("lista de diferenças de plataforma é inválida")

    save = pair.get("save")
    if not isinstance(save, dict):
        raise PlatformPairError("contrato de save ausente")
    if save.get("contract_id") != SAVE_CONTRACT_ID:
        raise PlatformPairError("identificador do contrato de save diverge")
    if save.get("schema_version") != 1:
        raise PlatformPairError("schema do save diverge")
    if save.get("remote_file_name") != REMOTE_FILE_NAME:
        raise PlatformPairError("objeto remoto do save diverge")

    platforms = pair.get("platforms")
    if not isinstance(platforms, dict):
        raise PlatformPairError("projetos de plataforma ausentes")
    for platform in ("windows", "android"):
        entry = platforms.get(platform)
        if not isinstance(entry, dict):
            raise PlatformPairError(f"projeto {platform} ausente")
        if not str(entry.get("project", "")).strip():
            raise PlatformPairError(f"nome do projeto {platform} ausente")
        resource_hash = entry.get("resource_car_sha256")
        if (
            not isinstance(resource_hash, str)
            or not SHA256_PATTERN.fullmatch(resource_hash)
        ):
            raise PlatformPairError(f"SHA-256 do CAR {platform} é inválido")
        if entry.get("save_contract_id") != SAVE_CONTRACT_ID:
            raise PlatformPairError(
                f"contrato de save declarado por {platform} diverge"
            )


def find_android_resource_car(apk_path: Path) -> tuple[str, bytes]:
    with zipfile.ZipFile(apk_path, "r") as apk:
        matches = [
            name
            for name in apk.namelist()
            if name == "assets/resource.car" or name.endswith("/resource.car")
        ]
        if len(matches) != 1:
            raise PlatformPairError(
                "o APK deve conter exatamente um assets/resource.car"
            )
        return matches[0], apk.read(matches[0])


def verify_artifacts(
    pair: dict[str, Any],
    windows_car: Path,
    android_apk: Path,
) -> dict[str, str]:
    validate_pair(pair)
    if not windows_car.is_file():
        raise PlatformPairError(f"CAR Windows ausente: {windows_car}")
    if not android_apk.is_file():
        raise PlatformPairError(f"APK Android ausente: {android_apk}")

    expected = pair["core"]["windows_reference_car_sha256"]
    windows_hash = sha256_file(windows_car)
    android_entry, android_car = find_android_resource_car(android_apk)
    android_hash = sha256_bytes(android_car)

    if windows_hash != expected:
        raise PlatformPairError(
            "o resource.car Windows não corresponde ao núcleo declarado"
        )
    if windows_hash != pair["platforms"]["windows"]["resource_car_sha256"]:
        raise PlatformPairError(
            "o resource.car Windows não corresponde ao projeto declarado"
        )
    if android_hash != pair["platforms"]["android"]["resource_car_sha256"]:
        raise PlatformPairError(
            "o resource.car Android não corresponde ao projeto declarado"
        )

    windows_entries = parse_car_payloads(windows_car.read_bytes())
    android_entries = parse_car_payloads(android_car)
    windows_names = [name for name, _ in windows_entries]
    android_names = [name for name, _ in android_entries]
    if windows_names != android_names:
        raise PlatformPairError(
            "ordem ou conjunto de entradas dos CARs diverge"
        )
    allowed = set(pair["core"]["allowed_platform_differences"])
    actual_differences = {
        name
        for (name, windows_payload), (_, android_payload)
        in zip(windows_entries, android_entries, strict=True)
        if windows_payload != android_payload
    }
    unexpected = sorted(actual_differences - allowed)
    if unexpected:
        raise PlatformPairError(
            "módulos comuns divergentes: " + ", ".join(unexpected)
        )

    return {
        "version": pair["version"],
        "version_code": str(pair["version_code"]),
        "save_contract_id": pair["save"]["contract_id"],
        "windows_resource_car_sha256": windows_hash,
        "android_resource_car_entry": android_entry,
        "android_resource_car_sha256": android_hash,
        "common_entries_verified": str(
            len(windows_entries) - len(actual_differences)
        ),
        "platform_differences": ",".join(sorted(actual_differences)),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verifica se Windows e Android usam o mesmo núcleo/save."
    )
    parser.add_argument("--pair", type=Path, required=True)
    parser.add_argument("--windows-car", type=Path, required=True)
    parser.add_argument("--android-apk", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    pair = json.loads(args.pair.read_text(encoding="utf-8"))
    result = verify_artifacts(pair, args.windows_car, args.android_apk)
    output = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(output, encoding="utf-8")
    else:
        sys.stdout.write(output)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, json.JSONDecodeError, zipfile.BadZipFile, PlatformPairError) as error:
        print(f"Erro: {error}", file=sys.stderr)
        raise SystemExit(1)
