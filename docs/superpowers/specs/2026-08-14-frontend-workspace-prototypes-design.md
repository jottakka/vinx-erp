# Workspace modular e protótipos funcionais do frontend

## Status

Desenho aprovado em revisão visual em 14 de agosto de 2026. A implementação é
empilhada sobre a entrega de shell da issue #168 e deve permanecer fora da
`main` até revisão e merge pelo responsável.

## Contexto

As PRs #185, #186 e #194 entregam preferências visuais, login de desenvolvimento,
shell responsivo, Início e Ajuda. Em 14 de agosto de 2026 elas continuam abertas
e empilhadas. A próxima evolução precisa melhorar a apresentação atual, preparar
o shell para módulos futuros e permitir avaliar jornadas planejadas sem fingir
que há backend ou dados reais.

O inventário foi conferido contra as issues atuais do GitHub com
`gpt-5.6-luna`. As superfícies documentadas são:

- destinos imediatos de preparação para Paguai, Atendimento, Kanban e Pessoas
  na issue #193;
- lista, formulário, conferência, fila financeira e políticas do Paguai nas
  issues #93, #96, #97 e #98;
- quadro e detalhe de card do Kanban nas issues #29, #30, #31 e #49;
- fila, detalhe e operação de Atendimento nas issues #33, #34 e #50;
- diretório, autoatendimento, revisão de RH e visão do gestor nas issues #35,
  #36, #37 e #51;
- inbox de notificações na issue #95;
- anexos seguros embutidos nas jornadas consumidoras nas issues #38, #39 e #52;
- agente supervisionado na issue #53 e no épico #10.

Não há issue atual que exija command palette. Também não há rota autônoma de
Arquivos: upload, processamento e acesso temporário pertencem à jornada de cada
módulo.

## Objetivos

1. Melhorar o shell atual com hierarquia visual clara, navegação escalável e
   componentes pequenos.
2. Oferecer somente tema claro e tema escuro, com uma paleta neutra e coerente.
3. Permitir abrir o agente como painel à direita ou como página dedicada sem
   perder a conversa local.
4. Criar protótipos navegáveis e interativos das telas documentadas para validar
   composição, responsividade e fluxo antes da integração com backend.
5. Separar estado de demonstração, apresentação e integração para que contratos
   reais substituam fixtures sem reescrever as páginas.

## Não objetivos

- Não criar endpoints, contratos, autorização, métricas ou dados de domínio.
- Não simular sucesso de API como se fosse produção.
- Não implementar runtime de agente, provider, memória, tools ou confirmação
  financeira real.
- Não criar PWA, cache offline, fila de escrita ou replay automático.
- Não criar command palette, rota de Arquivos ou chat empresarial.
- Não instalar uma biblioteca de layout sem necessidade atual.

## Direção visual

### Temas

O produto oferece apenas `light` e `dark`. A opção `system` deixa de aparecer
na interface e valores antigos ou inválidos são normalizados para `light`.

O tema claro usa off-white, superfícies brancas e texto grafite. O tema escuro
usa carvão, superfícies quase pretas e texto marfim. Um cobre discreto marca
foco, ação principal e orientação. Azul ou verde não dominam a interface.
Verde, amarelo e vermelho ficam reservados a estados semânticos.

A identidade da empresa aparece por nome, símbolo e badge. Ela não recolore o
shell nem comunica autorização por cor.

### Composição

- navegação lateral permanente em desktop e temporária em telas menores;
- cabeçalho compacto com contexto, tema, notificações e agente;
- conteúdo central pertencente à rota;
- painel utilitário opcional à direita para o agente;
- overlays pertencentes às features: drawers, dialogs, filtros e anexos.

## Arquitetura de componentes

```text
AppProviders
└── ProtectedRoute
    └── AppShell
        ├── AppNavigation
        ├── AgentPrototypeProvider (somente desenvolvimento)
        ├── NotificationPrototypeProvider (somente desenvolvimento)
        ├── AppHeader
        │   ├── ThemeToggle
        │   ├── NotificationTrigger
        │   └── AgentTrigger
        ├── ShellFeedbackRegion
        ├── RouteOutlet
        ├── NotificationDrawer
        └── AgentDock
```

O shell conhece apenas regiões e estado de apresentação. Ele não conhece regras
de Paguai, Kanban, Atendimento, Pessoas ou agente.

Componentes compartilhados devem representar padrões usados agora:

- `PageHeader` para título, descrição, contexto e ações;
- `PrototypeNotice` para identificar fixtures e estado local;
- `DetailDrawer` como estrutura de detalhe com foco e fechamento acessíveis;
- `DemoAttachmentField` para estados visuais de upload demonstrativo.

Status, filtros, vazios, tabelas/cards responsivos e dialogs usam diretamente
os componentes do MUI dentro de cada feature enquanto não houver uma segunda
composição idêntica comprovada.

Não haverá um componente genérico configurável para qualquer tela. Cada feature
compõe os padrões compartilhados e mantém seus próprios tipos, fixtures e
interações.

## Registro de rotas e navegação

Uma definição tipada única descreve caminho, rótulo, ícone, grupo e visibilidade.
O desktop, o drawer mobile, o estado ativo e o roteador consomem a mesma fonte.

As rotas de produto imediatas continuam honestas. Os protótipos ricos ficam em
um espaço explicitamente marcado como demonstração e são expostos somente no
ambiente de desenvolvimento. O build de produção não apresenta dados falsos.

Rotas planejadas para o laboratório local:

| Rota | Superfície |
| --- | --- |
| `/prototipos` | índice e explicação do modo demonstração |
| `/prototipos/paguai` | solicitações, conferência, financeiro e políticas |
| `/prototipos/kanban` | quadro, detalhe e movimento local de card |
| `/prototipos/atendimento` | fila, detalhe, timeline e comentário local |
| `/prototipos/pessoas` | diretório, ausências, revisão RH e visão do gestor |
| `/agente` | agente em página dedicada |

Notificações abrem em drawer do shell. Documentos aparecem em dialogs ou campos
de anexo dentro das features consumidoras.

## Modo demonstração

Toda tela com fixtures mostra `Dados de demonstração` de forma persistente. O
estado é somente local e reinicia ao recarregar, salvo preferências visuais.
Nenhuma interação afirma persistência ou resposta de API.

Cada feature mantém tipos, fixtures e um hook de estado local próprios. Não há
interface genérica de repositório. Quando existir contrato real, a integração
substitui esse hook por query/mutation da feature sem mover regras para o shell
nem reescrever os componentes de apresentação.

## Telas e interações

### Início

O Início mantém a leitura `all-authorized`, mostra empresas reais da sessão de
desenvolvimento e não inventa métricas operacionais. Cada card mantém seu
destino honesto de módulo em preparação e, somente em desenvolvimento, mostra
uma ação secundária `Abrir protótipo` quando o protótipo correspondente existir.

### Paguai

- alternar entre Solicitações, Conferência, Financeiro e Políticas;
- filtrar por empresa e status;
- abrir detalhe em drawer;
- abrir criação em dialog com empresa explícita;
- validar campos localmente sem afirmar criação real;
- revisar devolução, aprovação e pagamento em dialogs separados;
- apresentar anexo como estado demonstrativo de processamento.

### Kanban

- visualizar colunas e cards por empresa;
- abrir card em drawer;
- mover card por controle acessível e atualizar somente estado local;
- mostrar histórico local e marcar projeção Paguai como somente leitura.

### Atendimento

- filtrar fila e selecionar ticket;
- manter lista e detalhe lado a lado no desktop e em drawer no mobile;
- alternar comentário público ou interno;
- adicionar comentário local e atualizar timeline;
- apresentar SLA como dado demonstrativo, nunca inferência real.

### Pessoas

- pesquisar diretório e abrir perfil operacional;
- alternar Diretório, Ausências, Revisão RH e Equipe;
- criar rascunho local de ausência;
- demonstrar acesso restrito a documento sem expor conteúdo médico;
- manter a visão do gestor sem diagnóstico, CID ou notas médicas.

### Notificações

O gatilho no cabeçalho abre drawer com itens marcados como demonstração. Marcar
como lido altera somente estado local e informa isso ao usuário.

### Agente

`AgentConversationProvider` pertence à feature do agente e envolve as duas
apresentações:

- `AgentDock`: drawer persistente à direita em desktop e drawer temporário em
  telas menores;
- `AgentPage`: rota `/agente` para foco, comparação e conteúdo mais largo;
- `AgentConversation`: mensagens, fontes, estados tipados e composição comum;
- `AgentConfirmationCard`: proposta visual que nunca executa mutação.

O botão de expandir navega para `/agente`; voltar ao contexto reabre o painel
com a mesma conversa local. A empresa e a origem visual aparecem no cabeçalho.

O MUI 9.3.1 já instalado cobre `Drawer` com `anchor="right"`, variantes
`persistent` e `temporary` e comportamento responsivo. React Router cobre a
rota dedicada. `react-resizable-panels` foi avaliado e fica adiado: ele só será
adicionado se arrastar a largura virar requisito explícito.

## Acessibilidade e responsividade

- landmarks, títulos em ordem, link de salto e nomes acessíveis;
- foco inicial e retorno de foco em drawer/dialog;
- `Escape`, backdrop e botão explícito fecham overlays quando seguro;
- controles de movimento do Kanban funcionam por teclado, sem exigir drag;
- tabelas viram cards ou lista/detalhe em telas estreitas;
- o painel do agente vira overlay no mobile;
- contraste AA nos dois temas e zoom de 200% sem perda de conteúdo;
- estados não dependem exclusivamente de cor.

## Testes

- Vitest/Testing Library para registro de rotas, tema, shell, agente e cada
  interação local relevante;
- testes de foco/teclado para drawers e dialogs;
- Playwright para navegação entre todos os protótipos, tema claro/escuro,
  agente dock/página e viewport mobile;
- axe nas rotas principais;
- build de produção verifica que o índice de protótipos não fica visível.

## Divisão de entrega e estimativa

O conjunto completo ultrapassa 25 arquivos e 800 linhas manuais. Ele será
entregue em quatro PRs empilhados, cada um revisável:

1. **Shell neutro e pontos de extensão** — concluído com 21 arquivos fora de
   teste e 1.600–1.700 linhas manuais alteradas: temas, catálogo/registro de
   rotas, cabeçalho, navegação, Home, Ajuda e documentação. A fatia coesa
   ultrapassou o alvo consultivo de 800 linhas porque testes de acessibilidade
   e compatibilidade foram necessários para preservar menu, foco mobile,
   preferências e retornos seguros. O desenho já aprovado permanece o único
   registro de aprovação; esta estimativa não afirma aprovação humana adicional.
2. **Laboratório, Paguai e Kanban** — 20–25 arquivos, 700–1.100 linhas:
   infraestrutura de protótipos, solicitações, conferência, financeiro, quadro,
   detalhe e movimento local.
3. **Atendimento e Pessoas** — 14–20 arquivos, 600–1.000 linhas: fila, ticket,
   timeline, diretório, ausências, revisão RH e anexos contextualizados.
4. **Agente e notificações demonstrativas** — 16–22 arquivos, 650–1.000 linhas:
   drawer, página, conversa compartilhada, confirmação e inbox local.

Cada PR recebe testes focados, revisão independente e evidência temporária no
PR. A última branch integra as quatro fatias para avaliação visual local.

## Definição de pronto

- temas claro e escuro neutros e consistentes;
- nenhum azul ou verde dominante;
- navegação modular sem segundo registro;
- protótipos identificados como demonstração e indisponíveis na navegação de
  produção;
- agente funcional em painel e página com estado compartilhado;
- nenhum novo pacote sem uso comprovado;
- desktop, mobile, teclado, foco, contraste, loading, empty e error avaliados;
- testes públicos focados e `./scripts/verify-public.sh` executado antes do
  push da última mudança de comportamento;
- revisão por Qwen 3.8 Max do plano antes da implementação e revisão
  independente do código antes do merge.
