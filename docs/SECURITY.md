# Segurança e dados

## Nunca publicar

- saves reais;
- refresh tokens OAuth;
- arquivos de credenciais baixados do Google Cloud;
- chaves de assinatura Android;
- chave privada usada para assinar manifestos;
- artefatos originais que não tenham autorização de distribuição.

## Princípios

- save local é a fonte disponível offline;
- uploads usam revisão otimista e nunca sobrescrevem conflitos em silêncio;
- downloads são verificados antes de substituir qualquer arquivo;
- chaves privadas não são compiladas no cliente;
- tokens são armazenados no mecanismo seguro disponível em cada sistema;
- logs não registram tokens, conteúdo integral do save ou dados pessoais.

## Limite do SHA-256

O hash garante que o arquivo baixado corresponde ao manifesto recebido. Se um
invasor conseguir substituir o manifesto e o artefato, o hash sozinho não
protege a atualização. O fluxo de produção deverá validar uma assinatura
assimétrica do manifesto usando apenas uma chave pública embutida no cliente.

No Android há uma segunda barreira: o APK baixado precisa ter o mesmo pacote e
o mesmo conjunto de certificados da versão instalada. No Windows, enquanto os
executáveis e o manifesto não tiverem assinatura assimétrica, a proteção
continua limitada a HTTPS, tamanho e SHA-256. Não tratar essa fase como uma
cadeia de atualização autenticada contra comprometimento do manifesto.
