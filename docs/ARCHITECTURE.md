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
```

Android e save em nuvem estão congelados no escopo atual. Uma falha de rede
nunca impede o jogo local de ser aberto.

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

## Estados de origem

- Baseline privada: artefatos preservados e auditados fora deste repositório.
- Código autoral público: ferramentas, integração, contratos e modificações
  explicitamente documentadas.
- Builds: anexos de Releases, nunca arquivos comuns no histórico Git.
