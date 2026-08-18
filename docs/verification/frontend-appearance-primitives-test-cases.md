# Mapa de verificação: preferências visuais e identidade de empresa

Esta fatia cobre somente aparência local. Não autentica, não autoriza, não
persiste sessão nem define a empresa de uma ação.

| Classe BDD | Cenário | Teste ou caminho | Situação |
| --- | --- | --- | --- |
| Sucesso | Tema e densidade mudam pela interface nomeada e são restaurados | `AppearanceMenu`: `updates the selected theme and density`; `visual-theme-login.spec.ts`: recarga preserva preferências | Coberto |
| Validação/erro | Valor visual inválido no storage usa padrão seguro | `create-vinx-theme`: `returns MUI defaults when storage is blocked or contains an invalid mode`; `falls back to comfortable density when storage is malformed` | Coberto |
| Acesso | Preferências não concedem acesso, sessão ou empresa | `mock-session.spec.ts` (nome legado): `Scenario: Anonymous deep link returns safely to login` com BFF simulado; revisão manual de `visual-preferences.md` | Coberto pela fronteira existente |
| Estado/exceção | Empresa futura recebe slot estável; identificador ausente ou inválido usa neutro | `CompanyBadge`: `assigns a valid future company a deterministic non-neutral appearance`; `reserves neutral appearance for an absent or invalid company identifier` | Coberto |
| Concorrência | Seleções repetidas preservam só o estado final e um menu | `AppearanceMenu`: `keeps one menu and the final selection after repeated preference changes` | Coberto |
| Acessibilidade | Menu nomeado pelo acionador abre e devolve foco com teclado | `AppearanceMenu`: `names its menu from the trigger and returns focus after Escape`; Playwright: `keyboard closes the appearance menu and restores focus to its trigger`; `visual-theme-login.spec.ts` Axe | Coberto |

A central de ajuda cliente é parte de #168. Até ela chegar, o manual
`docs/manuals/frontend/visual-preferences.md` registra o procedimento durável
sem alegar suporte offline, autorização ou persistência de sessão.
