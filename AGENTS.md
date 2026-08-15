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
- Antes de implementar infraestrutura própria, avalie primeiro recursos nativos da plataforma, SDK oficial e biblioteca madura e mantida. Isso vale especialmente para identidade, validação, erros HTTP, rate limit, resiliência, workflows, mensageria/outbox, MCP, provedores de IA, telemetria, agendamento e serialização.
- Prefira substituir código próprio por solução consolidada quando ela reduzir materialmente código, risco ou manutenção e atender o caso de uso atual. Compatibilidade com uma abstração interna sem consumidor não é motivo para conservar complexidade.
- Código próprio no lugar de uma opção consolidada exige justificativa curta no issue ou PR: opções avaliadas, requisito não atendido e custo operacional aceito. Dependência sem uso atual continua proibida.
- Não adicione abstração, adaptador, skill ou dependência sem uso atual e motivo documentado.
- Autorização, contexto explícito de empresa, auditoria, observabilidade e documentação fazem parte da definição de pronto.
- Nunca inclua segredos, tokens, conteúdo médico, prompts ou conteúdo de arquivos em logs, fixtures ou Markdown.

## Logs operacionais

- Adicione logs estruturados em falhas, retries, transições de ciclo de vida e decisões operacionalmente relevantes. Para chamadas rotineiras a provedores externos, prefira trace e métrica de latência/erro; só registre log quando houver falha, mudança de estado ou decisão que exija investigação. Não registre entrada e saída de toda função ou requisição por padrão.
- Use `ILogger` com template constante e propriedades nomeadas. Inclua contexto mínimo útil: operação, módulo, resultado e motivo seguro. Propague `CorrelationId` pelo escopo de logging ou enriquecimento; não o repita manualmente em todo template. Para correlação autorizada, use identificador interno opaco somente quando necessário; nunca PII.
- Escolha nível pelo impacto: `Trace` para diagnóstico local muito detalhado; `Debug` para diagnóstico de desenvolvimento; `Information` para marco operacional ou de segurança relevante e esperado; `Warning` para anomalia recuperada, abuso, rate limit ou negação suspeita; `Error` para operação perdida, exceção não recuperada ou retry esgotado; `Critical` para indisponibilidade ampla, risco de integridade ou intervenção imediata. Não use `Warning` ou `Error` para validação comum esperada.
- Não duplique logs que middleware, biblioteca ou camada superior já emite com contexto equivalente. Preserve exceção e stack trace no ponto que decide a falha; não capture e relogue a mesma exceção em cascata.
- Auditoria é evidência durável de negócio e segurança; log operacional não a substitui. Mutação que exige auditoria mantém a mesma garantia transacional; tentativas negadas também geram auditoria quando a política exigir.
- Nunca registre JWT, refresh token, credencial, segredo, PII, conteúdo de arquivo, prompt ou payload bruto. Não use identificadores de alta cardinalidade ou dados controlados pelo cliente como rótulo de métrica.
- Ao alterar comportamento com sinal operacional relevante, cubra em teste o evento, o nível e as propriedades essenciais quando isso for critério de aceite; não fragilize testes por texto incidental da mensagem.

## Documentação

- Escreva documentação em português, com linguagem clara e compacta, mantendo apenas detalhes importantes e duráveis.
- Em C#, use XML `<summary>` compacto para métodos públicos ou helpers cuja intenção, garantia de segurança, concorrência ou transação não seja óbvia. Adicione `<param>` e `<returns>` somente quando ampliarem o contrato.
- Comentários inline devem explicar o motivo, a restrição ou a garantia relevante; não narre linha a linha nem repita o código. Prefira uma frase curta quando fizer sentido.

## Memória operacional

- Projectmem e Basic Memory são aids de desenvolvimento opcionais e opt-in.
  Leia docs/runbooks/projectmem.md e docs/runbooks/basic-memory.md antes de
  usá-los.
- Git, Markdown, testes, ADRs revisados e o estado vivo do GitHub continuam
  canônicos; abra a fonte citada antes de agir sobre memória.
- Para issues externas, siga o manifesto dry-run em
  docs/runbooks/github-memory-import.md. Não copie corpos nem habilite escrita
  automática. Nenhuma skill nova deve ser adicionada sem uso repetido e
  motivo documentado.
