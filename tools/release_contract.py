from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


SCHEMA_VERSION = 1
SUPPORTED_PLATFORMS = {"windows-x64", "android"}
SUPPORTED_CHANNELS = {"stable", "beta", "development"}
SEMVER_PATTERN = re.compile(
    r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$"
)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")


class ContractError(ValueError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_manifest(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ContractError(f"manifesto inválido: {exc}") from exc
    if not isinstance(data, dict):
        raise ContractError("o manifesto precisa ser um objeto JSON")
    return data


def validate_manifest(manifest: dict[str, Any]) -> None:
    required = {
        "schema_version",
        "version",
        "version_code",
        "channel",
        "published_at",
        "artifacts",
    }
    missing = sorted(required.difference(manifest))
    if missing:
        raise ContractError(f"campos obrigatórios ausentes: {', '.join(missing)}")

    if manifest["schema_version"] != SCHEMA_VERSION:
        raise ContractError("schema_version não suportado")
    if not isinstance(manifest["version"], str) or not SEMVER_PATTERN.fullmatch(
        manifest["version"]
    ):
        raise ContractError("version precisa seguir MAJOR.MINOR.PATCH")
    if (
        isinstance(manifest["version_code"], bool)
        or not isinstance(manifest["version_code"], int)
        or manifest["version_code"] < 1
    ):
        raise ContractError("version_code precisa ser inteiro positivo")
    if manifest["channel"] not in SUPPORTED_CHANNELS:
        raise ContractError("channel não suportado")
    if not isinstance(manifest["published_at"], str) or not manifest["published_at"]:
        raise ContractError("published_at precisa ser uma string não vazia")

    artifacts = manifest["artifacts"]
    if not isinstance(artifacts, list) or not artifacts:
        raise ContractError("artifacts precisa conter ao menos um artefato")

    platforms: set[str] = set()
    filenames: set[str] = set()
    for index, artifact in enumerate(artifacts):
        if not isinstance(artifact, dict):
            raise ContractError(f"artefato {index} não é um objeto")
        for field in ("platform", "filename", "url", "size", "sha256"):
            if field not in artifact:
                raise ContractError(f"artefato {index} não possui {field}")

        platform = artifact["platform"]
        filename = artifact["filename"]
        if platform not in SUPPORTED_PLATFORMS:
            raise ContractError(f"plataforma não suportada: {platform}")
        if platform in platforms:
            raise ContractError(f"plataforma duplicada: {platform}")
        platforms.add(platform)

        if (
            not isinstance(filename, str)
            or not filename
            or Path(filename).name != filename
        ):
            raise ContractError(f"filename inseguro no artefato {index}")
        if filename in filenames:
            raise ContractError(f"filename duplicado: {filename}")
        filenames.add(filename)

        parsed_url = urlparse(artifact["url"])
        if parsed_url.scheme != "https" or not parsed_url.netloc:
            raise ContractError(f"URL HTTPS inválida no artefato {index}")
        if (
            isinstance(artifact["size"], bool)
            or not isinstance(artifact["size"], int)
            or artifact["size"] < 1
        ):
            raise ContractError(f"tamanho inválido no artefato {index}")
        if not isinstance(artifact["sha256"], str) or not SHA256_PATTERN.fullmatch(
            artifact["sha256"]
        ):
            raise ContractError(f"SHA-256 inválido no artefato {index}")


def verify_artifacts(
    manifest: dict[str, Any], artifact_directory: Path
) -> list[str]:
    validate_manifest(manifest)
    errors: list[str] = []
    root = artifact_directory.resolve()

    for artifact in manifest["artifacts"]:
        path = (root / artifact["filename"]).resolve()
        if path.parent != root:
            errors.append(f"caminho fora da pasta permitida: {artifact['filename']}")
            continue
        if not path.is_file():
            errors.append(f"artefato ausente: {artifact['filename']}")
            continue
        actual_size = path.stat().st_size
        if actual_size != artifact["size"]:
            errors.append(
                f"tamanho divergente em {artifact['filename']}: "
                f"{actual_size} != {artifact['size']}"
            )
        actual_hash = sha256_file(path)
        if actual_hash != artifact["sha256"]:
            errors.append(f"SHA-256 divergente em {artifact['filename']}")

    return errors

