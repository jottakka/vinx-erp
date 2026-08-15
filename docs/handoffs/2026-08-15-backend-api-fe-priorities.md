# Prioridades de entrega — backend, API e FE

## Estado atual

As PRs #205 e #206 estão mescladas. A ordem abaixo parte desse estado.

## Princípios e limites

- Uma fatia vertical por PR: a baseline e cada issue abaixo permanecem em PRs separados; #90 e #117 também têm um PR por issue.
- Cada PR de implementação respeita até 25 arquivos contados, incluindo produção e documentação, e até 800 linhas manuais. Se exceder, a entrega deve ser dividida antes da implementação.
- O FE consome contratos estáveis de OpenAPI e bootstrap.
- API HTTP e MCP são transportes finos sobre os mesmos serviços de aplicação da Foundation; regras não são duplicadas por transporte.

## Grafo de dependências

```text
PRs #205/#206 mescladas
  -> baseline da plataforma ASP.NET
  -> #110 bootstrap autenticado
  -> #132 contexto de execução/task profile confiável
  -> libera lanes paralelas:
       - FE #90 -> #117
       - MCP #133 -> #125
       - agent runtime #135 -> #136

#132 + #135 + #136 + contrato de aprovação
  -> #138 API supervisionada

#201
  -> manutenção de baixa prioridade; não bloqueia nenhuma fase

spike explícito
  -> decisão sobre eventual framework de Worker/outbox
  -> decisão sobre eventual framework de workflow/checkpoint
```

## Ordem, fronteiras e sinais de conclusão

1. **Baseline da plataforma ASP.NET.** Um PR apenas para a base de backend, usando recursos nativos do framework. Conclui quando a base compila, passa pelas verificações públicas aplicáveis e está pronta para receber transportes finos.
2. **#110 — bootstrap autenticado.** Um PR para o serviço de aplicação da Foundation, neutro de transporte, mais um endpoint HTTP fino. Conclui quando o serviço é verificável sem HTTP, o endpoint autenticado o reutiliza e o contrato OpenAPI/bootstrap está estável para o FE.
3. **#132 — contexto confiável.** Um PR para o trusted execution context/task profile, sem incluir a API supervisionada. Conclui quando o contexto está disponível na fronteira dos serviços de aplicação e sua verificação não depende de um transporte específico.
4. **Lanes paralelas após #110 e #132.** As três lanes podem avançar entre si, mas preservam sua ordem interna e um PR por issue:
   - **FE #90 → #117:** #90 estabiliza bootstrap e estado autenticado; #117 só então substitui o mock pela sessão real, além das demais dependências declaradas na issue.
   - **MCP #133 → #125:** #133 usa `ModelContextProtocol.AspNetCore` para OAuth e Streamable HTTP, cria contexto confiável e não implementa tools; #125 adiciona `whoAmI` depois sobre esse transporte.
   - **Agent #135 → #136:** #135 integra os adapters previstos pela issue com SDKs oficiais; #136 valida, fundamenta e redige a saída real produzida por esses adapters.
5. **#138 — API supervisionada.** Um PR próprio, iniciado somente após contexto, provider, guardrails e contrato de aprovação estarem prontos. Conclui quando integra essas quatro dependências e passa pelas verificações públicas aplicáveis.
6. **#201 — manutenção.** Um PR próprio e de baixa prioridade. Conclui pelos critérios da issue, sem bloquear nem deslocar as fases anteriores.
7. **Frameworks futuros.** Worker/outbox e workflow/checkpoint exigem spikes explícitos antes de qualquer adoção. Cada spike conclui com evidência e decisão registrada de adotar ou não; adoção não faz parte das fases acima.

## Frameworks

| Momento | Primeira opção | Decisão |
|---|---|---|
| baseline | ASP.NET Core `AddProblemDetails`/exception middleware, Health Checks, Rate Limiting e Request Timeouts | substituir writers/DTOs de erro e endpoint de health próprios; preservar `code` de negócio como extensão de Problem Details e garantir log de exceção inesperada |
| baseline | Options pattern com `ValidateOnStart` | substituir parsing manual de configuração e validações repetidas; manter o invariante VINX de bucket privado |
| baseline | `Microsoft.AspNetCore.OpenApi` e `Microsoft.Extensions.Validation` do .NET 10 | gerar o contrato consumido pelo FE e validar DTOs de Minimal API sem filtro próprio |
| primeiro cliente externo | `Microsoft.Extensions.Http.Resilience` | usar pipeline mantido sobre Polly; retry apenas em operação segura/idempotente e sem empilhar handlers |
| #133 | `ModelContextProtocol.AspNetCore` oficial, transporte HTTP stateless | não criar protocolo, sessão ou dispatcher MCP próprio |
| #135 | SDK oficial do provedor atrás de `IChatClient` | não criar contrato paralelo de chat |
| primeiro outbox/worker durável | spike Wolverine PostgreSQL; comparar com MassTransit se surgir broker ou saga | adotar uma opção somente quando houver mensagem real e prova transacional; não instalar agora |
| #138, se houver pausa/reinício real | spike dos Workflows/checkpoints do Microsoft Agent Framework | não usar checkpoint em memória como durabilidade e não criar scheduler próprio antes do spike |

### ASP.NET Core Identity

Não adotar agora. Identity resolve contas locais, senha, login, roles, claims,
tokens e UI. O VINX já autentica JWT do Supabase e mantém apenas o vínculo
imutável da identidade externa com o usuário interno. Identity não substitui
esse JWT, memberships multiempresa, capabilities, RLS, auditoria ou aprovação
de negócio. Ele só volta à avaliação se o produto decidir hospedar contas e
credenciais localmente; nesse caso a mudança exige plano de migração de auth,
não uma simples troca de tabelas.

## Lanes de simplificação independentes

Estas reduções não bloqueiam a baseline e não entram no mesmo PR:

1. **Foundation approvals, antes de #138:** eliminar o pre-read da confirmação
   e a consulta preflight de proposta viva, usando os `UPDATE` condicionais e
   índices únicos já existentes. Preservar autorização-first, auditoria,
   classificação de replay/conflito e os testes PostgreSQL de concorrência.
2. **Contexto RLS:** centralizar somente os `set_config` idênticos na
   `FoundationRlsContext`; não criar Unit of Work ou pipeline genérico.
3. **Files:** remover capacidades de storage sem consumidor somente após
   confirmar que provisionamento de bucket pertence à infraestrutura.
4. **Frontend:** remover dependências sem import em PR exclusivo do FE, com
   `npm ci`, lint, testes e build.
