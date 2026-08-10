# Regras dos agentes - Vinx ERP

## Fluxo e escopo

- Nunca escreva diretamente na `main`; use uma branch `codex/*` e um pull request.
- Cada PR deve entregar uma única fatia vertical revisável.
- Antes de implementar, estime o diff e proponha issues filhas ou PRs empilhados quando houver mais de um subsistema independente, mais de 25 arquivos ou quando reunir, ao mesmo tempo, banco, API, provedor externo e CI sem dependência indivisível. O alvo de 800 linhas manuais orienta revisão e decomposição, mas não é bloqueio rígido: pode ser excedido quando a fatia continuar coesa e a justificativa for registrada na estimativa e no PR.
- Migrações, snapshots e lockfiles gerados não contam no limite de linhas, mas continuam no limite de arquivos e não justificam misturar responsabilidades. Exceções aos demais limites exigem aprovação explícita do responsável.
- Durante o desenvolvimento, execute testes públicos focados. Para mudanças de comportamento, execute a verificação pública completa uma vez antes do push; para documentação, use somente validações pertinentes.
- Use uma revisão independente por PR. Adicione outra apenas quando houver risco relevante de segurança ou integridade.
- Registre evidências temporárias no PR, não em relatórios versionados. Documente no repositório apenas decisões duráveis.
- Não troque modelo ou provedor durante uma tarefa sem aprovação explícita.

## Arquitetura e segurança

- Leia o desenho de arquitetura e a documentação do módulo antes de alterar comportamento.
- Não leia, liste nem execute `quality/private`; um verificador separado é responsável por esse escopo.
- Não adicione abstração, adaptador, skill ou dependência sem uso atual e motivo documentado.
- Autorização, contexto explícito de empresa, auditoria, observabilidade e documentação fazem parte da definição de pronto.
- Nunca inclua segredos, tokens, conteúdo médico, prompts ou conteúdo de arquivos em logs, fixtures ou Markdown.

## Documentação

- Escreva documentação em português, com linguagem clara e compacta, mantendo apenas detalhes importantes e duráveis.
