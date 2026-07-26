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
- sincronizar saves pelo Google Drive `appDataFolder`;
- documentar toda divergência deliberada da baseline privada.

## Estado atual

Bloco inicial concluído:

- contrato JSON de atualização;
- contrato JSON de metadados de save;
- gerador de manifesto de release;
- verificador de manifesto e artefatos;
- empacotador determinístico de atualização completa ou incremental;
- verificação e atualização integradas ao ponto de entrada Windows;
- pacote portátil Windows com inicializadores `.exe` compilados;
- verificação e instalação integradas ao ponto de entrada Android;
- sincronizador manual de save no Google Drive para Windows;
- testes automatizados;
- validação contínua pelo GitHub Actions.

O trabalho Android foi iniciado como um projeto de plataforma separado, preso
ao mesmo núcleo e ao mesmo contrato de save do Windows. Uma release conjunta
só é aceita quando o gate compara todas as entradas dos dois `resource.car`,
recusa divergências fora dos adaptadores de plataforma declarados e confirma
o contrato `mclonepc-game-cloud-v1`.

## Testes locais

```powershell
python -m unittest discover -s tests -v
python -m compileall -q tools tests
powershell -NoProfile -File tests/windows_updater_integration.ps1
powershell -NoProfile -File tests/windows_portable_integration.ps1
powershell -NoProfile -File tests/windows_cloudsave_integration.ps1
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

## Verificar o par Windows/Android

O manifesto privado do par registra os SHA-256 dos dois CARs, a lista explícita
de adaptadores permitidos e o mesmo objeto de save no Google Drive. O
verificador extrai `assets/resource.car` do APK e compara as entradas internas:

```powershell
python tools/verify_platform_pair.py `
  --pair "C:\build\platform-pair.json" `
  --windows-car "C:\build\windows\resource.car" `
  --android-apk "C:\build\android\MClonePC.apk" `
  --output "C:\build\platform-pair-verification.json"
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

## Pacote portátil Windows

O pacote portátil é montado a partir de uma pasta `game` local previamente
verificada. O conteúdo de terceiros não é adicionado ao repositório:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/build_windows_portable.ps1 `
  -SourceGameDirectory "C:\caminho\para\game" `
  -OutputRoot "C:\caminho\para\portable" `
  -Version "9.2.2.1" `
  -VersionCode 9020201 `
  -GoogleOAuthDesktopJson "C:\segredos\oauth-desktop.json"
```

O resultado possui dois pontos de entrada compilados:

- `MClonePC.exe`: verifica a versão em até 2,5 segundos, atualiza a pasta
  `game/` integralmente quando necessário e inicia `game/mclonepc.exe`;
- `MClonePC-Save-Nuvem.exe`: envia ou restaura manualmente o save privado no
  Google Drive `appDataFolder`.

Não existe um executável de atualização separado. A lógica interna permanece
em `updater/`, invisível ao uso normal. Falha de rede ou timeout são
`fail-open`: o jogo instalado abre normalmente. A tela preta com
`Atualizando...` só aparece depois que uma versão superior foi confirmada.

O sincronizador usa OAuth para aplicativo desktop com PKCE e não depende de
um segredo publicado no repositório. O Google exigiu o `client_secret` gerado
para este cliente específico; o empacotador lê um JSON local ignorado pelo Git
e o inclui somente no pacote privado. O refresh token local é protegido pelo
DPAPI do Windows. O jogo deve estar fechado para enviar ou restaurar.

Não há iniciadores `.bat` ou `.cmd`. A pasta inclui um manifesto SHA-256 de
todos os arquivos de `game`, estado inicial limpo, `backups/` e `work/` vazios,
um ZIP e o SHA-256 desse ZIP.

Alvo atual: Windows 10 ou 11 com .NET Framework 4.x habilitado. Os executáveis
não são assinados digitalmente nesta fase, portanto outro computador pode
exibir o aviso do Windows SmartScreen. O pacote completo deve permanecer local
enquanto contiver arquivos de terceiros.

## Entrada Android e atualização

O APK usa `UpdateGateActivity` como única atividade `MAIN/LAUNCHER`. Ela faz a
mesma consulta curta antes de iniciar o Solar2D. Sem atualização, sem rede ou
após 2,5 segundos, a atividade abre o jogo instalado. Quando existe versão
superior, mostra `Atualizando...`, baixa o APK, confere tamanho, SHA-256,
pacote, `versionCode` e o mesmo certificado da instalação atual.

O Android exige confirmação do usuário para instalar uma atualização fora de
uma loja. O aplicativo pode abrir essa confirmação, mas não pode ignorá-la.
Depois da instalação, tenta abrir a versão nova; algumas versões do Android
podem exigir que o usuário pressione **Abrir**.

## Licença

Uma licença para o código autoral ainda não foi escolhida. Publicar o
repositório permite leitura, mas não concede automaticamente licença sobre
arquivos de terceiros.
