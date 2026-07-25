# Arquitetura

## Separação de responsabilidades

```text
GitHub Repository
  código autoral, contratos, testes e documentação

GitHub Releases
  pacote incremental Windows, update.json e hashes

Instalação local
  launcher e atualizador fora da pasta game/
  game/ contém apenas a versão ativa
  backups/ contém até três versões anteriores

Pacote portátil local
  MClonePC.exe e MClonePC-Updater.exe compilados
  conteúdo completo mantido fora do repositório público
  ZIP acompanhado por SHA-256
```

Android continua congelado no escopo atual. O save em nuvem é um utilitário
externo e manual. Uma falha de rede nunca impede o jogo local de ser aberto.

## Fluxo de atualização

1. O cliente consulta a release mais recente.
2. Baixa `update.json`.
3. Valida `schema_version`, plataforma e compatibilidade.
4. Compara `version_code` com a instalação atual.
5. Baixa o artefato em um arquivo temporário.
6. Confere tamanho e SHA-256.
7. O atualizador externo cria uma cópia de trabalho e aplica o overlay.
8. Mantém a versão anterior disponível para recuperação.

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
  MClonePC-Updater.exe
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

Os arquivos visíveis usados para jogar, atualizar e sincronizar são executáveis
compilados. A lógica já auditada do atualizador permanece isolada em
`updater/`; ela não é o ponto de entrada do usuário. Reescrevê-la em outra
linguagem exigiria uma auditoria separada e não faz parte deste bloco.

O launcher calcula a raiz a partir da própria localização e inicia
`game/mclonepc.exe` com `game/` como diretório de trabalho. Portanto, a pasta
completa pode mudar de unidade ou diretório sem editar caminhos.

## Save em nuvem no Windows

`MClonePC-Save-Nuvem.exe` não é carregado pelo jogo e não modifica seus
bytecodes. Ele acessa o save vanilla em
`%APPDATA%\Robson\MClonePC\Documents` somente quando o processo
`mclonepc.exe` está fechado.

A autorização usa OAuth 2.0 para aplicativo desktop, PKCE S256 e callback
loopback. O cliente solicita somente `drive.appdata`. O refresh token é
protegido por DPAPI para o usuário atual do Windows; o pacote não contém
client secret.

O envio e a restauração são manuais. Antes de restaurar, o download, o
manifesto e todos os SHA-256 são validados. O save local anterior é copiado
para `backups/cloud-save/`, com retenção de três backups.

## Estados de origem

- Baseline privada: artefatos preservados e auditados fora deste repositório.
- Código autoral público: ferramentas, integração, contratos e modificações
  explicitamente documentadas.
- Builds: anexos de Releases, nunca arquivos comuns no histórico Git.
