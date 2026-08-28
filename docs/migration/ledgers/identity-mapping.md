# Ledger de identidade — issuer/subject ↔ `perfil_id` legado

Template para amarrar cada perfil legado ao par issuer/subject do provedor de identidade e à identidade externa já provisionada no Foundation. O `perfil_id` é a âncora de autoria em todas as tabelas legadas (`criado_por`, `responsavel_id`, etc.).

## Regra da célula vazia até a readback

As células **Issuer**, **Subject** e **Identidade externa (Foundation)** permanecem vazias até a readback do export de Auth capturado pelo responsável (ver runbook) e da lista real de perfis no snapshot. Não deduzir identidade de similaridade de nome/e-mail: sem leitura de retorno confirmada, a célula fica vazia e o registro correspondente não é importado como autorizado — cai em quarentena de importação para revisão.

## Aquisição dos insumos (bloqueados ao responsável)

O export de Auth (`auth.users`) requer acesso administrativo fornecido pelo responsável; os comandos exatos de captura somente-leitura estão no bundle do snapshot (`commands/auth-export.*`). Esta task não recebe nem armazena segredos ou tokens.

## Mapa

| `perfil_id` legado | E-mail legado (do snapshot) | Issuer | Subject | Identidade externa (Foundation) | Evidência da readback | Notas |
|---|---|---|---|---|---|---|
| _(uuid)_ | _(conforme snapshot)_ | | | | | |
| | | | | | | |

## Convenções

- Um `perfil_id` mapeia para **no máximo um** subject; duplicidades são conflito e resolvidas manualmente antes do import.
- Perfis órfãos (sem usuário ativo no Auth) recebem trilha separada: ou `restricted-archive` na classificação, ou reatribuição explícita de posse registrada nas notas.
- O hash da versão de mapeamento usado nas receipts do importador cobre também esta ledger: alterá-la entre execuções muda o hash e provoca conflito fail-closed nos lotes já importados.
