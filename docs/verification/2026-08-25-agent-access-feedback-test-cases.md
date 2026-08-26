# Casos de verificação — acesso do chat do agente

| Cenário | Resultado esperado | Evidência |
| --- | --- | --- |
| Maria em Alfa com `ai.read`/`Company` | Campo, anexos e envio permanecem habilitados; um envio usa `POST /agent/runs` | Vitest `sends the message to POST /agent/runs and renders the typed result with sources`; Playwright `Scenario: agent dock runs a typed agent run and shares the conversation with the dedicated page` e `Scenario: agent chat uploads, removes, and sends the remaining attachment in order` |
| Empresa sem `ai.read` | Aviso acionável, campo/anexo/envio desabilitados e zero requests | Vitest `blocks unavailable capability before any request`; Playwright `Scenario: agent access blocks chat without ai.read` |
| Sessão sem empresa | `Selecione uma empresa para usar o assistente.`, controles bloqueados e zero requests | Vitest `blocks all interaction with no company before any request` |
| Revogação após bootstrap | `capability_denied` vira mensagem amigável sem o código técnico | Vitest `renders a friendly capability denial returned after authorization` |
| Viewport estreita + acessibilidade | Sem overflow horizontal e sem violações axe no dock bloqueado | Playwright `Scenario: agent access blocks chat without ai.read` |

Comandos focados (Node isolado do laboratório):

```powershell
npm --workspace web run lint
npm --workspace web run format:check
npm --workspace web run test -- --run src/features/agent/agent.test.tsx -t "access|no company|document context|capability"
npm --workspace web run test:e2e:prototypes -- --grep "agent"
```

O snapshot padrão de Playwright inclui `ai.read` em escopo `Company`. O caso
bloqueado autentica Maria com o bootstrap padrão, substitui apenas a resposta
de `/foundation/bootstrap` já autenticada sem `ai.read` e recarrega a página.
Essa projeção testa a UX; ela não concede acesso nem substitui a revalidação
server-side do `POST /agent/runs`. O único grant correspondente é o fixture
determinístico `E2eFixtureSeeder`, coberto por
`SeedAsync_creates_the_rich_multi_company_dataset_atomically`; produção,
migrations, grants reais e catálogo permanecem intocados.

O cenário completo da suíte Vitest mantém uma flutuação preexistente no teste
de três anexos quando executado junto; ele passa isoladamente e não é usado
como evidência do bloqueio de acesso.
