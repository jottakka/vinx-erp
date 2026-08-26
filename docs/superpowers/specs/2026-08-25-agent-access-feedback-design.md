# Feedback de acesso do agente — desenho

## Status

Desenho aprovado para execução em 2026-08-25. O responsável pediu que a
mudança fosse feita no VINX ERP, em worktree nova, com implementação Terra,
revisão Sol e entrega em um único pull request.

## Problema

O workspace do agente é exibido em desenvolvimento mesmo quando a empresa
ativa não concede `ai.read` em escopo Company. O browser deixa o usuário
preencher e enviar a mensagem; somente depois o servidor responde
`denied/capability_denied`. A conversa transforma esse código interno em
"A execução foi negada por política. (capability_denied)", texto técnico que
não explica o que a pessoa pode fazer.

O mesmo padrão acrescenta outros reason codes em snake_case a mensagens de
validação, conflito e indisponibilidade. Esses códigos pertencem ao contrato e
à telemetria, não à interface de uma pessoa usuária.

## Decisão de produto

O gatilho do agente permanece visível e pode abrir o painel. Isso mantém o
recurso descobrível e permite explicar por que ele não está disponível. Dentro
do painel e da página dedicada, a conversa entra em estado bloqueado quando:

1. nenhuma empresa foi selecionada; ou
2. a empresa ativa não possui `ai.read` com escopo Company no bootstrap.

No estado bloqueado, campo de mensagem, envio e anexos ficam desativados. A UI
não inicia antiforgery, upload ou `POST /agent/runs`. O aviso é a explicação
principal e permanece visível no histórico vazio.

Não haverá botão fictício de solicitação de acesso: o projeto ainda não possui
uma jornada real para isso.

## Textos canônicos

| Situação | Texto apresentado |
|---|---|
| Sem empresa | `Selecione uma empresa para usar o assistente.` |
| Sem `ai.read` | `O assistente não está disponível para o seu perfil nesta empresa. Solicite acesso ao administrador da empresa.` |
| Empresa não autorizada, descoberta pelo servidor | `Você não tem acesso ao assistente nesta empresa. Selecione outra empresa ou solicite acesso ao administrador.` |
| Outra negação sem texto | `Não foi possível executar esta solicitação por uma regra de acesso.` |
| Provider indisponível | `O assistente está temporariamente indisponível. Tente novamente mais tarde.` |

Se uma resposta tipada `denied` trouxer texto seguro não nulo, esse texto pode
continuar sendo apresentado. Quando o texto for nulo, o reason code seleciona
uma das mensagens fixas acima, mas o código nunca é concatenado à mensagem.
Os demais estados fixos também deixam de mostrar sufixos técnicos em
snake_case.

## Fronteiras e autoridade

O frontend usa `hasCompanyCapability(company, "ai.read")` apenas como projeção
de UX autorizada pelo bootstrap. Essa verificação evita uma tentativa que já é
sabidamente impossível, mas não concede acesso. `POST /agent/runs` continua
revalidando sessão, membership, empresa e capability.

Se a permissão for revogada depois do bootstrap, a resposta
`denied/capability_denied` do servidor continua sendo aceita e vira a mesma
mensagem amigável. Nenhum fallback cliente transforma uma negação em sucesso.

## Interface

O provider expõe um estado discriminado único:

```ts
export type AgentAccess =
  | { available: true; message: null }
  | { available: false; message: string };
```

`AgentConversation` usa esse estado para o aviso e para desativar todos os
controles. `AgentDock`, `AgentPage` e `AgentTrigger` ajustam seus textos de
contexto sem ocultar o painel. A legenda do dock informa indisponibilidade; o
tooltip e o nome acessível do gatilho antecipam o estado.

## Testes e evidência visual

- Vitest prova que empresa com `ai.read` mantém o envio real existente.
- Vitest prova que empresa sem capability e sessão sem empresa exibem a cópia
  correta, desativam mensagem/anexo/envio e fazem zero requests.
- Vitest prova o fallback de revogação tardia e a ausência de
  `capability_denied`, `company_required` e `company_not_authorized` na UI.
- Playwright prova o estado bloqueado no dock, ausência de POST, acessibilidade
  axe e ausência de overflow em viewport estreita.
- O cenário Playwright autorizado recebe `ai.read` explicitamente na fixture;
  o grant existe somente no fixture determinístico `E2eFixtureSeeder` para
  Maria em Alfa, e não é concedido por migration ou produção. O teste
  `SeedAsync_creates_the_rich_multi_company_dataset_atomically` confirma o
  dataset; produção, grants reais e catálogo canônico ficam intocados.
- Inspeção manual registra screenshots desktop e estreita do estado bloqueado.

## Linha de base conhecida

Antes de qualquer edição, `agent.test.tsx` passou 21 de 22 casos em execução
completa. O caso de preservação de três anexos após navegação falhou por não
encontrar a lista já na página dedicada, mas passou imediatamente quando
executado isoladamente. A implementação não deve alterar essa semântica nem
usar a flutuação como evidência RED.

## Fora de escopo

- conceder `ai.read` em produção ou alterar migrations/grants;
- alterar autorização do backend ou o contrato HTTP;
- criar fluxo de solicitação/aprovação de acesso;
- habilitar o workspace do agente em build de produção;
- persistir conversa ou adicionar retry automático.
