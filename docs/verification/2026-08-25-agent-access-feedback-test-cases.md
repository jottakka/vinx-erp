# Casos de verificação — acesso do chat do agente

| Cenário | Resultado esperado | Evidência |
| --- | --- | --- |
| Maria em Alfa com `ai.read`/`Company` | Campo, anexos e envio permanecem habilitados; um envio usa `POST /agent/runs` | `agent.test.tsx`; Playwright `agent dock runs...` e `agent chat uploads...` |
| Empresa sem `ai.read` | Aviso acionável, campo/anexo/envio desabilitados e zero requests | `agent.test.tsx`; Playwright `agent access blocks chat without ai.read` |
| Sessão sem empresa | `Selecione uma empresa para usar o assistente.`, controles bloqueados e zero requests | `agent.test.tsx` |
| Revogação após bootstrap | `capability_denied` vira mensagem amigável sem o código técnico | `agent.test.tsx` |
| Viewport estreita + acessibilidade | Sem overflow horizontal e sem violações axe no dock bloqueado | Playwright `agent access blocks chat without ai.read` |

Comandos focados (Node isolado do laboratório):

```powershell
npm --workspace web run lint
npm --workspace web run format:check
npm --workspace web run test -- --run src/features/agent/agent.test.tsx -t "access|no-company|document context|capability"
npm --workspace web run test:e2e:prototypes -- --grep "agent"
```

O cenário completo da suíte Vitest mantém uma flutuação preexistente no teste
de três anexos quando executado junto; ele passa isoladamente e não é usado
como evidência do bloqueio de acesso.
