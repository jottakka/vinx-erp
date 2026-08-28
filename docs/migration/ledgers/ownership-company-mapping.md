# Ledger de posse — mapa legado → empresa alvo

Template para decidir, por chave de empresa do legado (`empresa_id`/`empresas_grupo`), qual empresa do ERP alvo recebe os registros. Insumos: snapshot imutável de produção (`tools/legacy-snapshot/`), agregados de `profile-tables.sh` e a contagem de linhas por tabela do bundle.

## Regra da célula vazia até a readback

Nenhuma célula **Empresa alvo** pode ser preenchida antes da readback do snapshot imutável de produção e da criação das empresas alvo no ERP. Célula vazia = importação bloqueada para as tabelas daquela empresa; o importador falha fechado (fail-closed) quando a empresa não está mapeada.

## Como preencher

1. Rodar o snapshot e os agregados de profiling e confirmar o inventário real de empresas/grupos (não inferir dos fontes).
2. Criar/identificar a empresa alvo no ERP e copiar seu id.
3. Registrar a evidência da decisão (contagens do snapshot que embasaram o agrupamento) na coluna Evidência.
4. Uma linha legada mapeia para **no máximo uma** empresa alvo; fusões devem ser decididas explicitamente em linha própria com justificativa.

## Mapa

| Chave legada | Identificação no snapshot | Empresa alvo (id no ERP) | Evidência da readback | Justificativa / notas |
|---|---|---|---|---|
| _(uuid `empresa_id`)_ | _(nome/grupo conforme snapshot)_ | | | |
| | | | | |

## Tabelas afetadas

Toda tabela classificada `canonical` em `classification-ledger.md` com coluna de empresa herda este mapa. A tabela alvo da fatia inicial é a staging de Customers (exemplo guiado `clientes`); as demais entram vertical a vertical depois do contrato alvo.
