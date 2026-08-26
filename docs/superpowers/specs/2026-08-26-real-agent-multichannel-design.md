# Agente real e multicanal — desenho

## Estado

Desenho aprovado em conversa quanto ao objetivo, à entrega em uma única PR e
ao uso direto da API OpenCode Go. Este arquivo aguarda a revisão final do
responsável antes do plano de implementação.

## Objetivo

Entregar um único núcleo de Agente para Web, WhatsApp e Telegram, com texto e
OCR seguro de documentos, capaz de produzir uma proposta tipada de rascunho
Pagaí. O modelo nunca escolhe empresa, nunca executa ferramenta e nunca cria a
solicitação financeira sem confirmação humana autenticada no Web/BFF.

## Decisões obrigatórias

- A aplicação chama a API remota OpenCode Go diretamente. Harness, serviço web
  do Harness, OpenCode CLI e subprocessos de provider não participam do runtime.
- O provider é `openai_compatible`, com base
  `https://opencode.ai/zen/go/v1` e modelo
  `deepseek-v4-flash-vision-exp`. A chamada usa Chat Completions; o prefixo
  `opencode-go/` pertence somente à configuração interna do OpenCode e não vai
  no campo `model` da API.
- A chave entra somente no processo servidor por
  `VINX_OPENCODE_GO_API_KEY`. Ela não vem de arquivo do Harness, argumento CLI,
  `.env` versionado, browser, banco, fixture, log ou documentação.
- O mesmo modelo atua nos perfis server-side `AgentRun` e `AgentRun:Vision`.
  Somente o segundo aceita imagem canônica para OCR.
- Testes automatizados usam exclusivamente o modelo determinístico. O modo
  real existe apenas para demonstração manual e para o smoke sintético
  autorizado.
- A entrega será uma única PR, por instrução explícita do responsável. Essa
  decisão é a exceção aprovada aos limites usuais de arquivos e linhas.

Referência pública do contrato atual do provider:
<https://opencode.ai/docs/go/>.

## Superfície compartilhada

O módulo AI passa a receber um comando normalizado, independente de transporte:

```csharp
public sealed record AgentCommand(
    ActorContext Actor,
    Guid CompanyId,
    string Journey,
    string Message,
    IReadOnlyList<Guid> AttachmentIds,
    Guid RequestId,
    AgentChannelContext Channel)
{
    public Guid CorrelationId => Actor.CorrelationId;
}

public sealed record AgentChannelContext(
    AgentChannelKind Kind,
    Guid? InboundMessageId);
```

`AgentChannelKind` aceita somente `Web`, `WhatsApp` e `Telegram`.
`InboundMessageId` é um identificador interno opaco; telefone, `wa_id`,
`phone_number_id`, `chat_id`, username, token e conteúdo bruto não entram no
comando como autorização nem em logs.

Uma única aplicação compartilhada executa, nesta ordem:

1. validação estrutural, do journey e hash canônico do comando;
2. claim durável da execução antes de OCR ou provider;
3. resolução do contexto confiável;
4. membership ativa e capabilities da empresa explícita;
5. reautorização de cada `FileRecord`;
6. seleção server-side do perfil, endpoint e modelo;
7. OCR/contexto efêmero, quando aplicável;
8. no máximo uma chamada ao modelo principal;
9. gate de saída e resultado normalizado;
10. persistência do resultado seguro, auditoria e intenção de resposta no
    mesmo commit.

O `POST /agent/runs` continua como adapter Web e traduz seu contrato HTTP para
`AgentCommand`. WhatsApp e Telegram carregam o ator, a empresa e o conteúdo
somente do registro inbound já admitido; nenhum adapter acessa provider, chave,
OCR ou Pagaí diretamente.

Admissões de canal publicam um envelope comum contendo apenas canal e
`InboundMessageId`. O handler compartilhado faz claim idempotente, relê o
registro autorizado e só então constrói `AgentCommand`. O recibo técnico atual
do Telegram conserva seu significado; a execução do Agente usa um estado e um
envelope separados, sem transformar `completed` em efeito de negócio.

### Ledger de execução

O ledger tem unicidade por ator, empresa, canal e request ID, além do hash do
payload. Seus estados são `claimed`, `vision_in_flight`, `model_in_flight`,
`completed`, `failed_closed` e `ambiguous`.
Chamadas externas também recebem sub-registro único por execução, papel
(`vision` ou `main`) e ordinal; PDF multipágina pode ter um ordinal por página,
enquanto a chamada principal usa ordinal zero.

- mesmo request ID e hash em `completed` devolve o resultado persistido;
- mesmo request ID com outro hash retorna `idempotency_conflict`;
- concorrente que encontra `claimed`/`*_in_flight` não chama OCR/provider;
- imediatamente antes de cada chamada externa, a transição para o estado
  `*_in_flight` e o respectivo sub-registro são persistidos;
- crash, cancelamento local ou timeout após `*_in_flight` produz
  `ambiguous`; nunca há retry cego do provider;
- falha comprovadamente anterior a qualquer chamada pode fechar como
  `failed_closed` e exige novo request ID para nova tentativa;
- exatamente-uma-vez do provider não é alegada: a garantia é no máximo uma
  tentativa automática por claim, com resultado externo incerto explicitado;
- a intenção de resposta de canal entra em outbox idempotente, única por
  execução e tipo de resultado.

Para Web, `claimed` ou `*_in_flight` retorna `execution_in_progress` e
`ambiguous` retorna `execution_ambiguous`, sem nova chamada. Para canais,
redelivery nesses estados é no-op e preserva evidência para reconciliação.

## Provider e transporte

O adapter existente `OpenAiCompatibleChatClient` continua sendo a única
implementação de protocolo. Um resolver interno lê exclusivamente
`VINX_OPENCODE_GO_API_KEY` do ambiente do processo, exige valor não vazio em
uma linha e rejeita controles. `AgentRun__ApiKey`, appsettings, argumentos e
demais fontes de `IConfiguration` não podem fornecer nem sobrescrever a chave.
O valor entra em um tipo interno de credencial sensível e é entregue em memória
aos perfis de texto e visão, sem propriedade pública/bindable de runtime.

ApiHost recebe a chave para runs Web; WorkerHost recebe a chave para runs de
WhatsApp e Telegram. Nenhum outro host/processo a recebe. A composição comum do
módulo AI mantém endpoint, modelo, limites e transporte idênticos nos dois
hosts.

Cada `HttpClient` do provider usa `SocketsHttpHandler` dedicado com:

- HTTPS e porta 443;
- host exato `opencode.ai`;
- `AllowAutoRedirect = false`;
- `UseProxy = false`;
- cookies desabilitados;
- `ConnectCallback` que resolve DNS e valida cada A/AAAA imediatamente antes
  da conexão;
- rejeição de loopback, privado, link-local, metadata, CGNAT, multicast,
  reserved, unspecified e IPv4-mapped inseguro;
- timeout server-side, cancelamento propagado e zero retry automático.

A falha de configuração usa somente `agent_configuration_failed`. Falhas de
execução permanecem em categorias estáveis e redigidas. Nenhuma exceção pública
carrega body, prompt, resposta, imagem, header ou cadeia interna do provider.
O launcher remove a chave do ambiente dos processos Vite e de qualquer filho
que não componha o servidor; readiness e diagnóstico validam somente presença,
nunca valor ou propriedades derivadas.

## Texto e OCR

O pipeline existente `DocumentContext` permanece canônico:

- recebe apenas `FileRecord.Id`;
- reautoriza ator, empresa, membership e `foundation.files.read-ocr`;
- exige arquivo confirmado, retenção viva, checksum/tamanho coerentes e
  veredito antimalware durável em produção;
- aceita somente PDF, JPEG e PNG nas classificações allow-listed;
- usa texto local do PDF primeiro;
- rasteriza somente páginas sem texto;
- canonicaliza imagem, remove metadados e aplica tetos de pixels/bytes;
- envia somente a imagem canônica ao perfil `AgentRun:Vision`;
- exige JSON OCR fechado;
- marca o texto como `untrusted` e descarta bytes/contexto após o run.

O fixture manual pode usar storage local e scanner `clean` test-only apenas
com documentos sintéticos. A UI e o runbook deixam explícito que isso não prova
Supabase Storage nem ClamAV de produção.

## Journey Pagaí

`paguai_from_document_v1` aceita exatamente um anexo autorizado, classificado
como `agentic_attachment`, e retorna um envelope JSON fechado. O caminho
genérico de criação Pagaí continua exigindo `paguai_request_invoice`; somente a
confirmação vinculada a uma proposta válida pode associar `agentic_attachment`
ao rascunho, após reautorizar o mesmo arquivo:

```json
{
  "status": "proposal",
  "reason": null,
  "proposal": {
    "title": "string",
    "description": "string",
    "amount": "250.00",
    "currency": "BRL",
    "dueDate": "2026-09-10",
    "beneficiaryDisplayName": "string",
    "proofRequirement": "optional",
    "invoiceFileId": "11111111-1111-4111-8111-111111111111",
    "confidence": 0.9,
    "sourceReferences": [
      { "field": "amount", "source": "11111111-1111-4111-8111-111111111111:p1" },
      { "field": "dueDate", "source": "11111111-1111-4111-8111-111111111111:p1" },
      { "field": "beneficiaryDisplayName", "source": "11111111-1111-4111-8111-111111111111:p1" }
    ]
  }
}
```

O envelope alternativo também mantém todas as chaves:

```json
{
  "status": "needs_clarification",
  "reason": "not_financial_document",
  "proposal": null
}
```

Regras do gate Pagaí:

- não aceita chave desconhecida, ausente ou tipo divergente;
- `proposal` é integralmente preenchida no estado `proposal`; não há defaults;
- `amount` passa por `PaguaiAmountRules`, sem arredondamento;
- `currency` é exatamente `BRL`;
- `dueDate` passa pelo parser `DateOnly` canônico;
- `proofRequirement` aceita somente `optional` ou `required`;
- `invoiceFileId` deve ser exatamente o único anexo autorizado;
- confidence fica entre 0 e 1; abaixo de `0.80` vira
  `needs_clarification`;
- valor, vencimento e beneficiário exigem referência opaca para fonte/página
  autorizada; nenhum trecho OCR é persistido;
- `sourceReferences.field` aceita somente `amount`, `dueDate` e
  `beneficiaryDisplayName`, com ao menos uma referência para cada campo;
- conta bancária, CNPJ, fornecedor ou data sem evidência fecham como
  esclarecimento;
- `reason` aceita somente `not_financial_document`, `insufficient_evidence`,
  `low_confidence`, `schema_invalid` ou `document_context_unavailable`;
- documento não financeiro fecha como `not_financial_document`.

## Proposta e confirmação Web

Uma proposta aprovada pelo gate é persistida no módulo Pagaí por 15 minutos,
com somente:

- IDs opacos de proposta, ator, empresa, request e `FileRecord`;
- campos tipados e seguros mostrados na revisão;
- referências opacas de fonte, confidence, versão de política e hash canônico;
- criação, expiração, consumo e resultado idempotente.

Os estados persistidos são `pending`, `consumed`, `rejected` e `invalidated`;
expiração é calculada pelo relógio do banco e torna `pending` indisponível para
qualquer claim. Um índice único garante no máximo uma proposta viva por
ator/empresa/request e `paguai.requests.source_proposal_id` garante no máximo um
rascunho por proposta.

Prompt, OCR bruto, resposta bruta e bytes nunca são persistidos. A tabela tem
RLS forçada e uma proposta só é lida pelo ator na empresa autorizada. Durante o
gate, cada `sourceReference` é comparada com o conjunto efêmero exato de
`FileRecord`/página produzido pelo OCR; referência inventada fecha o run sem
persistir proposta.

A UI abre uma rota autenticada de revisão. Um serviço dedicado
`PaguaiProposalConfirmationService` é dono da única transação; ele reutiliza
validadores/fábricas puras do módulo Pagaí, mas não chama
`PaguaiDraftService.CreateAsync`, que abre sua própria transação. O confirm
recebe um novo request ID, trava a proposta e, na mesma transação:

1. revalida sessão, ator, empresa e membership;
2. exige `paguai.draft.create` em escopo Company;
3. revalida `ai.read`, `foundation.files.read-ocr` e o mesmo `FileRecord`,
   incluindo retenção, estado e empresa;
4. promove, por operação interna auditada, a classificação
   `agentic_attachment` para `paguai_request_invoice`; nenhuma outra origem ou
   transição é aceita;
5. revalida hash, política, expiração e estado não consumido;
6. insere um único rascunho com `source_proposal_id` único;
7. marca a proposta como consumida e vincula o rascunho;
8. grava idempotência e auditoria;
9. confirma a transação.

Falha em qualquer passo faz rollback completo. Repetir o mesmo request ID
devolve o mesmo rascunho; mudar payload com o mesmo ID retorna conflito;
confirmações concorrentes produzem um único vencedor.
A revisão confirma exatamente a proposta persistida; ela não permite alterar
campos silenciosamente. Correção humana rejeita a proposta e usa o fluxo manual
Pagaí, preservando a distinção entre proposta do modelo e entrada do usuário.

`POST /paguai/draft-proposals/{id}/reject` exige BFF, antiforgery, o mesmo ator
e empresa, request ID e proposta viva. A transição `pending -> rejected` é
terminal, idempotente e auditada. Confirm e reject após expiração retornam
`proposal_unavailable`; a expiração usa o relógio do PostgreSQL e é terminal
por leitura/claim, sem job obrigatório. Membership/capability/arquivo revogado
impede a criação, registra negação segura e invalida a proposta. GET, confirm e
reject preservam autenticação BFF, antiforgery nas mutações e RLS.

## Adapters de canal

### Web

O BFF fornece o ator. A empresa é escolhida entre memberships autorizadas. O
endpoint revalida tudo e continua aceitando apenas IDs opacos de anexos.

### WhatsApp

O webhook mantém HMAC `X-Hub-Signature-256`, `phone_number_id` exato,
deduplicação e correlação durável. Somente uma admissão com contato interno,
membership e empresa inequívocos cria `AgentCommand`. Documento passa primeiro
por download, scan, storage e `FileRecord`; o adapter chama o núcleo apenas com
o ID confirmado. O fluxo direto atual de OCR/provider no adapter deixa de ser
autoridade. A barreira `reply_in_flight`, `reply_ambiguous` e a reconciliação
manual atuais permanecem; timeout ou transporte incerto nunca reenviam
automaticamente.

### Telegram

O webhook mantém o segredo, bot/endpoint e admissão já mergeados. O worker usa
somente o registro inbound opaco e a identidade vinculada. Username, nome e
texto livre nunca resolvem ator ou empresa.

O recibo `completed` continua significando apenas consumo técnico. Um contrato
separado `claim/read/finalize` controla `agent_pending`, `agent_in_flight`,
`agent_completed` e `agent_ambiguous`, sempre por `InboundMessageId`, sem
conteúdo no envelope. A entrega usa o outbox e provider Telegram existentes;
resultado externo incerto fica ambíguo e não recebe retry cego.

### Empresa ambígua e Pagaí

Identidade inexistente, não verificada, inativa ou ligada a mais de um usuário
fica fail-closed e não recebe resposta. Quando existe exatamente um usuário
interno, mas mais de uma empresa autorizada, a admissão registra
`selection_required` com IDs internos e não chama o modelo. Um link opaco leva
ao BFF; após login, o servidor exige que a sessão corresponda ao usuário,
permite escolher somente membership ativa e persiste preferência revogável por
canal. Só uma nova mensagem, após a escolha, inicia execução.

Para proposta Pagaí, os canais devolvem apenas “Documento pronto para revisão”
e uma rota Web allow-listed; não incluem valor, favorecido, vencimento ou
documento e nunca confirmam por mensagem.

Links usam somente a origem HTTPS configurada no servidor e rotas internas
allow-listed; nenhuma URL do usuário é refletida. A resposta WhatsApp preserva
a barreira `reply_in_flight`; Telegram usa o outbox/provider existente. A chave
de idempotência de saída deriva do `InboundMessageId` e do tipo de resultado,
de modo que redelivery não chama o modelo nem envia uma segunda resposta.
O mapper limita resposta a 800 caracteres de texto simples e acrescenta links
internos somente a partir de rotas geradas pelo servidor; o modelo nunca fornece
URL de revisão.

## Launcher e smoke real

Os modos são separados:

```bash
npm run dev:real-e2e -- --profiles maria,clara --agent-mode deterministic
npm run dev:real-e2e -- --profiles maria,clara --agent-mode real
```

O modo determinístico não faz rede externa. O modo real:

- exige `VINX_OPENCODE_GO_API_KEY` antes de iniciar Docker;
- fixa endpoint/modelo/perfis somente no processo servidor;
- não registra `DeterministicE2eAgentModel`;
- mostra aviso de envio de conteúdo sintético, sem provider metadata sensível;
- publica somente readiness sanitizada;
- suporta exatamente um smoke textual e um smoke de visão autorizados;
- não envia mensagem real por WhatsApp ou Telegram.

Em deploy normal, ApiHost e WorkerHost usam o provider real quando a
configuração aprovada está presente; sem ela, falham fechados. A restrição
“somente demonstração/smoke” vale para o launcher descartável. O modelo
determinístico só pode ser registrado por assemblies/entrypoints de teste e
nunca por configuração de produção.

O seed test-only usa o mesmo PostgreSQL compartilhado e somente dados
sintéticos:

- Maria/Alfa recebe `ai.read`, `foundation.files.upload`,
  `foundation.files.confirm`, `foundation.files.read-ocr` e
  `paguai.draft.create` para o fluxo autorizado;
- Maria/Beta permanece sem a capability necessária e não alcança o modelo;
- Clara/Gama permanece sem a capability necessária e não alcança o modelo;
- um documento financeiro sintético percorre upload, confirmação, OCR,
  proposta e confirmação Pagaí;
- nenhuma chave, token de canal, telefone, chat ID, prompt, imagem real,
  resposta bruta ou connection string completa entra no banco.

## Testes e evidência

RED/GREEN cobrirá as cinco classes do projeto:

- sucesso: Web, WhatsApp e Telegram chegam ao mesmo núcleo; texto e OCR reais
  são provados somente no smoke sintético;
- validação/erros: configuração ausente, schema fechado, documento não
  financeiro, baixa confidence, limites e falha de dependência;
- acesso: não autenticado, capability ausente, tenant errado, identidade não
  vinculada, empresa ambígua e anexo estrangeiro não chamam o provider;
- estado/exceções: proposta viva, expirada, consumida, revogada, arquivo
  removido, rejeição explícita e rollback de confirmação;
- concorrência: replay, conflito de payload, confirmação concorrente e
  redelivery de canal não duplicam tentativa automática, resposta ou rascunho;
- ambiguidade externa: crash/timeout após `*_in_flight` não produz retry cego
  e exige reconciliação ou novo request explícito.

O fechamento usa projetos afetados e consumidores transitivos. Como a mudança
cruza AI, ApiHost, WorkerHost, Notifications, Pagaí, Web, fixture, banco e
segurança transversal, haverá um único `./scripts/verify-public.sh` local após
o freeze e depois CI. Docker será executado somente após atribuição de todos os
processos, worktrees e recursos VINX.

## Exclusões

- Harness, OpenCode CLI ou provider via subprocesso.
- Histórico multi-turno, memória longa, vector store e streaming.
- Tool execution automática.
- Visão geral de fotografias, objetos ou cenas.
- Confirmação financeira nativa por WhatsApp ou Telegram.
- Mensagem real para destinatário externo durante testes.
- Segredos, conteúdo real, PII ou payload bruto em fixture/evidência.

## Estimativa e PR

Estimativa corrigida após revisão Sol, recalculada antes da implementação:

- 55–75 arquivos contados fora de testes;
- 6.500–9.000 linhas manuais, incluindo testes e SQL escrito à mão em
  migrations;
- 5–8 arquivos genuinamente gerados de migration/snapshot/lockfile,
  reportados à parte;
- uma única PR vertical, inicialmente empilhada sobre a PR #332 e retargetada
  para `main` após o merge da dependência.

O tamanho excede os limites normais porque a entrega explicitamente reúne
runtime real, núcleo compartilhado, três adapters, proposta financeira, UI,
fixture e prova de segurança. O responsável pediu uma única PR; não haverá
refactor, documentação ou capacidade sem relação direta com essa fatia.
Antes do código, uma única issue registrará objetivo, escopo, estimativa, as
cinco classes BDD e a exceção de PR único. Qualquer adiamento real receberá
issue vinculada, alvo e condição de reavaliação.
