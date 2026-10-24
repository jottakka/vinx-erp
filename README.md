# Vinx ERP

Vinx ERP is an internal, multi-company modular monolith. The legacy
React/Vite/Supabase project is reference-only; new development follows the
versioned architecture and repository policies below.

## Route map

- [Architecture design](docs/architecture/2026-08-08-vinx-erp-architecture-design.md)
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

## Ordem de entrega atual

A preparação local desta issue #11 não cria um frontend de produto. A fundação
de autorização segue em [#19](https://github.com/jottakka/vinx-erp/issues/19),
[#20](https://github.com/jottakka/vinx-erp/issues/20) e
[#68](https://github.com/jottakka/vinx-erp/issues/68). Com esses contratos
disponíveis, o primeiro consumidor de frontend é
[#16](https://github.com/jottakka/vinx-erp/issues/16), seguido do bootstrap em
[#90](https://github.com/jottakka/vinx-erp/issues/90). Essa ordem evita que o
browser invente escopo de empresa, autorização ou acesso direto a providers.
