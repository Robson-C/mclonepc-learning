from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from release_contract import (
    ContractError,
    SUPPORTED_PLATFORMS,
    sha256_file,
    validate_manifest,
)


def parse_artifact(value: str) -> tuple[str, Path]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("use PLATAFORMA=CAMINHO")
    platform, raw_path = value.split("=", 1)
    if platform not in SUPPORTED_PLATFORMS:
        raise argparse.ArgumentTypeError(f"plataforma não suportada: {platform}")
    path = Path(raw_path)
    if not path.is_file():
        raise argparse.ArgumentTypeError(f"arquivo não encontrado: {path}")
    return platform, path


def build_manifest(
    *,
    version: str,
    version_code: int,
    repository: str,
    artifacts: list[tuple[str, Path]],
    channel: str,
    release_notes: str,
) -> dict:
    tag = f"v{version}"
    manifest_artifacts = []
    for platform, path in artifacts:
        filename = path.name
        manifest_artifacts.append(
            {
                "platform": platform,
                "filename": filename,
                "url": (
                    f"https://github.com/{repository}/releases/download/"
                    f"{tag}/{filename}"
                ),
                "size": path.stat().st_size,
                "sha256": sha256_file(path),
            }
        )

    manifest = {
        "schema_version": 1,
        "version": version,
        "version_code": version_code,
        "channel": channel,
        "published_at": datetime.now(timezone.utc).isoformat().replace(
            "+00:00", "Z"
        ),
        "release_notes": release_notes,
        "artifacts": manifest_artifacts,
    }
    validate_manifest(manifest)
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Gera update.json com tamanho e SHA-256 dos artefatos."
    )
    parser.add_argument("--version", required=True)
    parser.add_argument("--version-code", required=True, type=int)
    parser.add_argument("--repository", required=True)
    parser.add_argument(
        "--channel",
        choices=("stable", "beta", "development"),
        default="development",
    )
    parser.add_argument("--release-notes", default="")
    parser.add_argument(
        "--artifact",
        action="append",
        required=True,
        type=parse_artifact,
        help="Pode ser repetido. Formato: PLATAFORMA=CAMINHO",
    )
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    try:
        manifest = build_manifest(
            version=args.version,
            version_code=args.version_code,
            repository=args.repository,
            artifacts=args.artifact,
            channel=args.channel,
            release_notes=args.release_notes,
        )
    except ContractError as exc:
        parser.error(str(exc))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"manifesto criado: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

