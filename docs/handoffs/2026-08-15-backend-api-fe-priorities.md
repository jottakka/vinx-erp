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
       - FE #90/#117
       - MCP #133
       - adapters de providers #135
       - output guardrails #136

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
4. **Lanes paralelas após #110 e #132.** FE (#90/#117), MCP #133, providers #135 e guardrails #136 podem avançar em paralelo, sem compartilhar PR:
   - **FE #90/#117:** consome os contratos estáveis de OpenAPI/bootstrap; cada issue conclui com seus próprios critérios e verificações.
   - **MCP #133:** usa o `ModelContextProtocol.AspNetCore` oficial; conclui quando o primeiro `whoAmI`, somente leitura, chama o mesmo serviço de aplicação usado pela API.
   - **Providers #135:** usa SDKs oficiais e `IChatClient`; conclui quando o adapter previsto pela issue está integrado e verificado.
   - **Output guardrails #136:** conclui quando os critérios e verificações da issue estão atendidos, sem antecipar #138.
5. **#138 — API supervisionada.** Um PR próprio, iniciado somente após contexto, provider, guardrails e contrato de aprovação estarem prontos. Conclui quando integra essas quatro dependências e passa pelas verificações públicas aplicáveis.
6. **#201 — manutenção.** Um PR próprio e de baixa prioridade. Conclui pelos critérios da issue, sem bloquear nem deslocar as fases anteriores.
7. **Frameworks futuros.** Worker/outbox e workflow/checkpoint exigem spikes explícitos antes de qualquer adoção. Cada spike conclui com evidência e decisão registrada de adotar ou não; adoção não faz parte das fases acima.

## Frameworks

**Aprovados agora:** recursos nativos do ASP.NET Core para a baseline; `ModelContextProtocol.AspNetCore` oficial em #133; SDKs oficiais e `IChatClient` em #135.

**Somente após spike:** frameworks de Worker/outbox e de workflow/checkpoint.
