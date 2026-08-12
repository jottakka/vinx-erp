# Mapa de verificação: preferências visuais e identidade de empresa

Esta fatia cobre somente aparência local. Não autentica, não autoriza, não
persiste sessão nem define a empresa de uma ação.

| Classe BDD | Cenário | Teste ou caminho | Situação |
| --- | --- | --- | --- |
| Sucesso | Tema e densidade mudam pela interface nomeada e são restaurados | `AppearanceMenu`: `updates the selected theme and density`; Playwright: `reload returns to /login while the selected visual mode restores` | Coberto |
| Validação/erro | Valor visual inválido no storage usa padrão seguro | `create-vinx-theme`: `returns MUI defaults when storage is blocked or contains an invalid mode`; `falls back to comfortable density when storage is malformed` | Coberto |
| Acesso | Preferências não concedem acesso, sessão ou empresa | `mock-session.spec.ts`: `redirects an anonymous protected deep link to login`; revisão manual do texto de `visual-preferences.md` | Coberto pela fronteira existente |
| Estado/exceção | Empresa futura recebe slot estável; identificador ausente usa neutro | `CompanyBadge`: `resolves a valid future company to a deterministic local appearance`; `uses neutral appearance when company identifier is absent` | Coberto no PR dependente #167; esta primitiva permanece definida aqui |
| Concorrência | Seleções repetidas preservam só o estado final e um menu | `AppearanceMenu`: `updates the selected theme and density and closes after each selection`; Playwright: `keyboard closes the appearance menu and restores focus to its trigger` | Coberto |
| Acessibilidade | Menu nomeado abre e devolve foco com teclado | Playwright: `keyboard closes the appearance menu and restores focus to its trigger`; `visual-theme-login.spec.ts` Axe | Coberto |

A central de ajuda cliente é parte de #168. Até ela chegar, o manual
`docs/manuals/frontend/visual-preferences.md` registra o procedimento durável
sem alegar suporte offline, autorização ou persistência de sessão.
