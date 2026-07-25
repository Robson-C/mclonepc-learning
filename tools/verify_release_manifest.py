from __future__ import annotations

import argparse
from pathlib import Path

from release_contract import ContractError, load_manifest, verify_artifacts


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Valida update.json e os artefatos da release."
    )
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--artifact-directory", required=True, type=Path)
    args = parser.parse_args()

    try:
        manifest = load_manifest(args.manifest)
        errors = verify_artifacts(manifest, args.artifact_directory)
    except ContractError as exc:
        print(f"ERRO: {exc}")
        return 1

    if errors:
        for error in errors:
            print(f"ERRO: {error}")
        return 1

    print(
        f"OK: versão {manifest['version']} "
        f"({len(manifest['artifacts'])} artefato(s))"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

