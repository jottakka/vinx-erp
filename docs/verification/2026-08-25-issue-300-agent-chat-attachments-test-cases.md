# Issue #300 — anexos no chat do agente

## Escopo

Esta matriz cobre somente o cliente web: seleção, upload em três etapas e o
envio de referências opacas ao `POST /agent/runs`. Backend, OCR, persistência,
retry, WebP e credenciais de storage permanecem fora desta fatia.

## Casos executáveis

| Área | Cenário | Evidência |
| --- | --- | --- |
| Client | Tipo, tamanho zero e abort após digest falham antes de rede. | `agent-attachments-client.test.ts` |
| Client | SHA-256, payload da intenção, antiforgery por mutação, PUT sem credenciais e confirmação produzem somente metadados seguros. | `agent-attachments-client.test.ts` |
| Client | URL não segura, resposta/mismatch inválido, HTTP de storage, rede e abort convergem em falha tipada, sem retry nem corpo. | `agent-attachments-client.test.ts` |
| Provider/UI | Três arquivos preservam ordem e estado; quarto arquivo não inicia upload; falho e pendente bloqueiam envio; remoção é individual. | `agent.test.tsx` |
| Provider/UI | Reset, unmount e troca de membership abortam upload e descartam resposta tardia; dock e página usam a mesma seleção. | `agent.test.tsx` |
| Agent client | Sem IDs, o corpo histórico não muda; com IDs, a cópia em ordem segue no request; `document_context_unavailable` mantém reason e orientação fixa. | `agent-client.test.ts`, `agent.test.tsx` |
| Browser | Rotas antiforgery, intenção, PUT, confirmação e run são interceptadas; após remover o primeiro arquivo, somente o segundo ID alcança o run. | `module-prototypes.spec.ts` |

## Limites verificados

- A URL de upload é transitória: não integra resultado, erro, estado da
  conversa, renderização ou documentação operacional.
- O browser nunca envia arquivo, checksum ou detalhes de storage ao
  `POST /agent/runs`; o corpo contém apenas IDs confirmados.
- O fluxo não faz retry automático. Uma falha permanece removível, e nova
  tentativa exige uma nova seleção explícita.
