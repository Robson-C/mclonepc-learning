from __future__ import annotations

import argparse
import json
import stat
import zipfile
from pathlib import Path

from release_contract import SEMVER_PATTERN, sha256_file


PACKAGE_SCHEMA_VERSION = 1
FIXED_ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


class PackageError(ValueError):
    pass


def _safe_relative_path(path: Path, root: Path) -> str:
    try:
        relative = path.relative_to(root).as_posix()
    except ValueError as exc:
        raise PackageError(f"arquivo fora do payload: {path}") from exc
    if not relative or relative.startswith("/") or ".." in Path(relative).parts:
        raise PackageError(f"caminho inseguro: {relative}")
    return relative


def collect_payload(payload_directory: Path) -> list[dict[str, object]]:
    root = payload_directory.resolve()
    if not root.is_dir():
        raise PackageError(f"pasta de payload inexistente: {root}")

    files: list[dict[str, object]] = []
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise PackageError(f"links simbólicos não são aceitos: {path}")
        if not path.is_file():
            continue
        relative = _safe_relative_path(path.resolve(), root)
        files.append(
            {
                "path": relative,
                "size": path.stat().st_size,
                "sha256": sha256_file(path),
            }
        )
    if not files:
        raise PackageError("o payload não contém arquivos")
    return files


def build_package_manifest(
    *,
    payload_directory: Path,
    version: str,
    version_code: int,
    supported_from_version_codes: list[int],
    mode: str = "full-replacement",
) -> dict[str, object]:
    if not SEMVER_PATTERN.fullmatch(version):
        raise PackageError("version precisa seguir MAJOR.MINOR.PATCH")
    if version_code < 1:
        raise PackageError("version_code precisa ser positivo")
    supported = sorted(set(supported_from_version_codes))
    if not supported or any(value < 1 for value in supported):
        raise PackageError("informe ao menos um version_code de origem válido")
    if any(value >= version_code for value in supported):
        raise PackageError("versões de origem precisam ser menores que o destino")
    if mode not in {"full-replacement", "incremental-overlay"}:
        raise PackageError(f"modo de pacote não suportado: {mode}")

    return {
        "schema_version": PACKAGE_SCHEMA_VERSION,
        "mode": mode,
        "version": version,
        "version_code": version_code,
        "supported_from_version_codes": supported,
        "files": collect_payload(payload_directory),
    }


def _write_zip_bytes(
    archive: zipfile.ZipFile, archive_name: str, payload: bytes
) -> None:
    info = zipfile.ZipInfo(archive_name, FIXED_ZIP_TIMESTAMP)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = (stat.S_IFREG | 0o644) << 16
    archive.writestr(info, payload)


def write_package(
    *,
    payload_directory: Path,
    output: Path,
    manifest: dict[str, object],
) -> None:
    root = payload_directory.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w") as archive:
        manifest_bytes = (
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
        ).encode("utf-8")
        _write_zip_bytes(archive, "package.json", manifest_bytes)
        for entry in manifest["files"]:
            relative = entry["path"]
            _write_zip_bytes(
                archive,
                f"payload/{relative}",
                (root / relative).read_bytes(),
            )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Cria um pacote seguro para o atualizador Windows."
    )
    parser.add_argument("--payload-directory", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--version-code", required=True, type=int)
    parser.add_argument(
        "--from-version-code",
        required=True,
        action="append",
        type=int,
        help="Pode ser repetido para permitir mais de uma versão instalada.",
    )
    parser.add_argument(
        "--mode",
        choices=("full-replacement", "incremental-overlay"),
        default="full-replacement",
        help=(
            "full-replacement substitui integralmente a pasta game; "
            "incremental-overlay preserva arquivos ausentes do payload."
        ),
    )
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    try:
        manifest = build_package_manifest(
            payload_directory=args.payload_directory,
            version=args.version,
            version_code=args.version_code,
            supported_from_version_codes=args.from_version_code,
            mode=args.mode,
        )
        write_package(
            payload_directory=args.payload_directory,
            output=args.output,
            manifest=manifest,
        )
    except PackageError as exc:
        parser.error(str(exc))

    print(
        f"pacote criado: {args.output} "
        f"({len(manifest['files'])} arquivo(s))"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

