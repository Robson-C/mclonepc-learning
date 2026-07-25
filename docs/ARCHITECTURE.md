# Arquitetura

## Separação de responsabilidades

```text
GitHub Repository
  código autoral, contratos, testes e documentação

GitHub Releases
  APK, pacote Windows, update.json e hashes

Instalação local
  executável/aplicativo e save local persistente

Google Drive appDataFolder
  cópias privadas e versionadas dos saves do usuário
```

Atualização do aplicativo e sincronização de save são fluxos independentes.
Uma falha de rede nunca deve impedir o jogo de carregar o save local.

## Fluxo de atualização

1. O cliente consulta a release mais recente.
2. Baixa `update.json`.
3. Valida `schema_version`, plataforma e compatibilidade.
4. Compara `version_code` com a instalação atual.
5. Baixa o artefato em um arquivo temporário.
6. Confere tamanho e SHA-256.
7. Solicita instalação no Android ou aciona o atualizador externo no Windows.
8. Mantém a versão anterior disponível para recuperação.

SHA-256 detecta corrupção, mas não substitui assinatura criptográfica. A
assinatura do manifesto será adicionada antes de distribuir instaladores para
terceiros.

## Fluxo de save

1. O jogo grava o save local de forma atômica.
2. Um snapshot é empacotado e recebe SHA-256.
3. O cliente consulta os metadados remotos.
4. Se a revisão remota for a esperada, envia o novo snapshot.
5. Se houver divergência, preserva ambos e apresenta a escolha ao usuário.
6. Mantém backups anteriores para rollback.

O formato inicial trata o save como um pacote opaco. A infraestrutura não
altera campos internos nem infere o significado de arquivos originais.

## Plataformas

- Windows: cliente OAuth do tipo Desktop e atualizador externo.
- Android: cliente OAuth Android e instalador do sistema para APK assinado.
- Os dois clientes OAuth devem pertencer ao mesmo projeto Google Cloud.

## Estados de origem

- Baseline privada: artefatos preservados e auditados fora deste repositório.
- Código autoral público: ferramentas, integração, contratos e modificações
  explicitamente documentadas.
- Builds: anexos de Releases, nunca arquivos comuns no histórico Git.

