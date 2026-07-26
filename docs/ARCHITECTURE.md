# Arquitetura

## Separação de responsabilidades

```text
GitHub Repository
  código autoral, contratos, testes e documentação

GitHub Releases
  pacote completo Windows, APK Android, update.json e hashes

Instalação local
  MClonePC.exe é o único ponto de entrada visível
  lógica interna do atualizador fora da pasta game/
  game/ contém apenas a versão ativa
  backups/ contém até três versões anteriores

Pacote portátil local
  MClonePC.exe com verificação inicial integrada
  conteúdo completo mantido fora do repositório público
  ZIP acompanhado por SHA-256
```

Windows e Android são projetos de plataforma separados, mas não mantêm cópias
independentes do jogo. Os dois recebem o mesmo núcleo versionado, o mesmo
contrato `mclonepc-game-cloud-v1` e o mesmo nome de objeto remoto
`mclonepc-game-cloud-v1.json`. Somente empacotamento, assinatura, integração
OAuth e código estritamente dependente da plataforma podem divergir.

Antes de publicar uma versão conjunta, `verify_platform_pair.py` valida o
SHA-256 de cada `resource.car`, extrai o CAR do APK e compara todas as entradas
internas. Somente adaptadores de plataforma previamente declarados podem
divergir; qualquer diferença no núcleo compartilhado reprova a versão. O gate
também recusa contratos de save diferentes. Uma falha de rede nunca impede o
jogo local de ser aberto.

```text
Núcleo privado versionado
  resource.car, assets, dados e contrato de save
             |
             +-- projeto Windows: PE, launcher e OAuth desktop
             |
             +-- projeto Android: APK, assinatura e OAuth Android
```

## Fluxo de atualização

1. O cliente consulta a release mais recente.
2. Baixa `update.json`.
3. Valida `schema_version`, plataforma e compatibilidade.
4. Compara `version_code` com a instalação atual.
5. Baixa o artefato em um arquivo temporário.
6. Confere tamanho e SHA-256.
7. No Windows, o componente interno monta uma nova pasta `game/`; um pacote
   `full-replacement` não preserva arquivos obsoletos.
8. No Android, `PackageInstaller` valida e substitui o APK depois da
   confirmação obrigatória do usuário.
9. Mantém a versão Windows anterior disponível para recuperação.

A consulta inicial tem limite rígido de 2,5 segundos e é `fail-open`: falha de
rede, timeout ou manifesto inválido não bloqueiam a versão instalada. A tela
preta `Atualizando...` só é exibida depois de confirmar uma versão superior.

SHA-256 detecta corrupção, mas não substitui assinatura criptográfica. A
assinatura do manifesto será adicionada antes de distribuir instaladores para
terceiros.

## Separação física no Windows

```text
merchant_clone/github/mclonepc-learning/
  repositório, scripts e preparação das próximas versões

merchant_clone/local_install/MClonePC/
  Jogar MClonePC.cmd
  Atualizar MClonePC.cmd
  updater/
  game/
  state/
  backups/
  work/
```

O atualizador nunca executa a build localizada no repositório. Ele só modifica
`game/` dentro da instalação local explicitamente indicada.

## Distribuição portátil

```text
MClonePC Portable vX.Y.Z/
  MClonePC.exe
  MClonePC-Save-Nuvem.exe
  cloud-save.json
  LEIA-ME.txt
  portable-package.json
  game-files.sha256
  updater/
    MClonePC-Updater.ps1
  game/
  state/
    installed.json
  backups/
  work/
```

Os arquivos visíveis usados para jogar e sincronizar são executáveis
compilados. A lógica de atualização permanece isolada em `updater/`, mas é
chamada automaticamente pelo próprio `MClonePC.exe`; ela não é um ponto de
entrada separado do usuário.

O launcher calcula a raiz a partir da própria localização e inicia
`game/mclonepc.exe` com `game/` como diretório de trabalho. Portanto, a pasta
completa pode mudar de unidade ou diretório sem editar caminhos.

## Entrada Android

O APK declara `UpdateGateActivity` como a única atividade
`MAIN/LAUNCHER`. `CoronaActivity` permanece no manifesto sem o filtro de
inicialização. Quando não existe atualização, o gate abre diretamente a
atividade do Solar2D.

Quando existe atualização, o gate valida o artefato Android do mesmo
`update.json`, baixa para o cache e exige coincidência exata de pacote,
`versionCode` e conjunto de certificados antes de criar uma sessão
`PackageInstaller`. A confirmação visual do instalador é uma exigência do
Android para aplicativos comuns distribuídos fora de loja.

## Transporte de nuvem Android

`plugin.mclonecloud.LuaLoader` liga o módulo Lua adaptado à implementação
Android. `AndroidCloudClient` pede autorização no momento em que o usuário toca
em conectar, por meio de Google Identity Services `AuthorizationClient`.
Depois do consentimento, o token de acesso é usado diretamente nas chamadas
REST do Drive.

O transporte Android preserva o mesmo arquivo, payload, revisão e
`appProperties` do transporte Windows. A autorização é própria da plataforma:
não há processo auxiliar, servidor de loopback, refresh token autoral ou
`client_secret` no APK. O Google Play Services conserva a concessão e fornece
um token atualizado nas operações seguintes.

## Save em nuvem no Windows

`MClonePC-Save-Nuvem.exe` não é carregado pelo jogo e não modifica seus
bytecodes. Ele acessa o save vanilla em
`%APPDATA%\Robson\MClonePC\Documents` somente quando o processo
`mclonepc.exe` está fechado.

A autorização usa OAuth 2.0 para aplicativo desktop, PKCE S256 e callback
loopback. O cliente solicita somente `drive.appdata`. O refresh token é
protegido por DPAPI para o usuário atual do Windows. O Google exigiu o
`client_secret` deste cliente desktop na troca de token. Ele é lido de um JSON
local fora do repositório e existe somente no pacote privado; não é uma chave
capaz de substituir o consentimento OAuth do usuário.

O envio e a restauração são manuais. Antes de restaurar, o download, o
manifesto e todos os SHA-256 são validados. O save local anterior é copiado
para `backups/cloud-save/`, com retenção de três backups.

## Estados de origem

- Baseline privada: artefatos preservados e auditados fora deste repositório.
- Código autoral público: ferramentas, integração, contratos e modificações
  explicitamente documentadas.
- Builds: anexos de Releases, nunca arquivos comuns no histórico Git.
