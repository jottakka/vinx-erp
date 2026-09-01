# Private quality custody

## Purpose

`quality/private` contains verifier-owned abuse, regression, and evaluation
tests that complement the public quality suite. It is a process boundary, not
a security boundary; repository access alone cannot technically enforce it.

## Roles and access

- Only the designated quality author and verifier administer private test
  sources, fixtures, configuration, and execution.
- Builders must not read, list, modify, or execute content in this directory.
- The verifier runs private checks in a separate execution context from the
  builder's worktree and public test commands.

## Reporting

When a private check fails, the verifier reports only redacted, actionable
evidence needed to correct production behavior: outcome, affected public
contract, correlation identifier when available, and the minimum safe failure
description. Reports must not disclose private source, scenario wording,
secrets, credentials, medical data, prompt bodies, or file contents.

## Test scope

Private tests focus on abusive or adversarial paths, including forged or
missing company context, cross-company access attempts, authorization bypass,
and sensitive-data disclosure. They do not replace public unit, integration,
BDD, browser, or contract tests; every behavior with a builder-facing contract
still needs appropriate public coverage.

## Delivered slice: Foundation abuse coverage (first slice)

`Vinx.Erp.PrivateQualityTests` now carries the verifier-authored Foundation
slice promised above: one real Postgres 17 container (Testcontainers), the same
role bootstrap the public harness uses (`infra/database/bootstrap-foundation-roles.sql`),
migrations applied by `FoundationDbContext` as `vinx_migrator`, and a minimal
Wolverine storage skeleton plus `infra/database/grant-wolverine-runtime.sql` so
the runtime/worker grant boundary exists. Hostile connections are `vinx_runtime`
and `vinx_worker`, never the superuser. The project references
`Vinx.Erp.Foundation` only as a build-time migration provider; every assertion
is raw Npgsql/SQL authored by the verifier.

Coverage (8 tests, single serialized collection):

- Forged `app.company_id` / `app.authorized_company_ids` against another
  company's rows in `foundation.memberships`, `foundation.companies`,
  `demandas.demands`, `service_desk.atendimento_cards`, `paguai.requests`, and
  the notification inbox function — every visible victim row is a failure.
- Missing context entirely: zero rows everywhere and a rejected tenant INSERT
  (WITH CHECK, SQLSTATE 42501); no direct grant on the inbox table; the inbox
  function without an actor must fail closed.
- Escalation: `foundation.provision_platform_organization` without context and
  with a forged operator identity at `app.mfa_assurance='aal1'` must both be
  denied; cross-schema Wolverine inserts (`vinx_runtime` into the worker
  durability, `vinx_worker` into the ApiHost durability) denied by grants;
  `UPDATE`/`DELETE` on `audit.audit_events` denied (append-only).

Run it from the verifier execution context (requires Docker):

```sh
export VINX_QUALITY_VERIFIER=1
dotnet restore quality/private/Vinx.Erp.PrivateQualityTests/Vinx.Erp.PrivateQualityTests.csproj --locked-mode
./scripts/verify-private.sh
```

`scripts/verify-private.sh` runs `dotnet test` on the csproj directly (the
project stays excluded from `Vinx.Erp.sln` by custody policy). The GitHub
workflow `.github/workflows/private-quality.yml` runs the same suite when
Actions billing returns.


## Operating rule

Private test execution is independently authorized and recorded. Any needed
production fix is implemented and verified through the public suite first;
the verifier then reruns the private suite and communicates only a redacted
result.
