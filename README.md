# MClonePC Learning

Projeto educacional para estudar portabilidade, atualização multiplataforma e
sincronização segura de saves em um jogo Solar2D.

O repositório começa pela infraestrutura autoral. Ele **não contém** a
baseline executável, bytecodes, assets, saves ou credenciais do Merchant RPG
original.

## Objetivos

- distribuir versões de PC e Android por GitHub Releases;
- detectar atualizações por um manifesto versionado;
- verificar tamanho e SHA-256 antes de instalar;
- preservar saves locais durante atualizações;
- sincronizar saves entre PC e Android pelo Google Drive `appDataFolder`;
- documentar toda divergência deliberada da baseline privada.

## Estado atual

Bloco inicial concluído:

- contrato JSON de atualização;
- contrato JSON de metadados de save;
- gerador de manifesto de release;
- verificador de manifesto e artefatos;
- empacotador determinístico de atualizações incrementais;
- instalador e atualizador externos para Windows;
- testes automatizados;
- validação contínua pelo GitHub Actions.

Android e save em nuvem estão fora do escopo atual.

## Testes locais

```powershell
python -m unittest discover -s tests -v
python -m compileall -q tools tests
powershell -NoProfile -File tests/windows_updater_integration.ps1
```

## Gerar um manifesto

```powershell
python tools/build_release_manifest.py `
  --version 0.1.0 `
  --version-code 100 `
  --repository Robson-C/mclonepc-learning `
  --artifact windows-x64=artifacts/MClonePC-Windows.zip `
  --artifact android=artifacts/MClonePC-Android.apk `
  --output artifacts/update.json
```

## Verificar uma release

```powershell
python tools/verify_release_manifest.py `
  --manifest artifacts/update.json `
  --artifact-directory artifacts
```

## Instalação local separada

O repositório de desenvolvimento e a instalação executável não compartilham a
mesma pasta. Para criar uma instalação a partir de uma fonte local previamente
verificada:

```powershell
powershell -NoProfile -File windows/Install-Local.ps1 `
  -SourceGameDirectory "C:\caminho\para\build-verificada" `
  -InstallRoot "C:\caminho\para\instalacao\MClonePC" `
  -Version "9.2.0" `
  -VersionCode 90200
```

Na instalação:

- `Jogar MClonePC.cmd` abre somente o jogo instalado;
- `Atualizar MClonePC.cmd` consulta a última GitHub Release;
- `game/` contém a versão ativa;
- `backups/` mantém no máximo três versões anteriores;
- `work/` é temporário;
- `state/installed.json` registra a versão instalada.

## Licença

Uma licença para o código autoral ainda não foi escolhida. Publicar o
repositório permite leitura, mas não concede automaticamente licença sobre
arquivos de terceiros.
