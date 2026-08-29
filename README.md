# Vinx ERP

Vinx ERP is an internal, multi-company modular monolith. The legacy
React/Vite/Supabase project is reference-only; new development follows the
versioned architecture and repository policies below.

## Route map

- [Architecture design](docs/superpowers/specs/2026-08-08-vinx-erp-architecture-design.md)
- [Local-development runbook](docs/runbooks/local-development.md)
- [ADR index](docs/adr/)
- [Public verification](docs/runbooks/public-verification.md)

Read [AGENTS.md](AGENTS.md) before changing behavior. It defines the required
authorization, company-context, audit, observability, documentation and
sensitive-data rules.

## Verification and merge policy

Run [public verification](docs/runbooks/public-verification.md) before a pull
request. GitHub Free may not offer the private-repository branch protections
needed to enforce every desired rule, so agents never push or merge `main`;
the repository owner performs reviewed merges deliberately.

## Módulos atuais

- **Foundation** (`src/Foundation`): identidade, autorização, empresa explícita,
  auditoria, arquivos e notificações — fonte dos contratos compartilhados.
- **Módulos de domínio** (`src/Modules`): `Vinx.Erp.AI`, `Vinx.Erp.Atendimento`,
  `Vinx.Erp.Demandas`, `Vinx.Erp.Notifications` e `Vinx.Erp.Paguai`.
- **Hosts** (`src/Hosts`): `ApiHost` (BFF + API de domínio) e `WorkerHost`
  (filas Wolverine, outbox, DLQ).
- **Web** (`apps/web`): rotas operacionais `/paguai`, `/atendimento`,
  `/demandas`, `/pessoas`, `/permissoes`, `/notificacoes` e `/agente`.

Ordem de entrega, issues e histórico por fatia ficam nos issues do GitHub e em
`docs/superpowers/plans`; este README descreve apenas o estado atual.
