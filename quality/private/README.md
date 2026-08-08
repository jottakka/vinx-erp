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

## Operating rule

Private test execution is independently authorized and recorded. Any needed
production fix is implemented and verified through the public suite first;
the verifier then reruns the private suite and communicates only a redacted
result.
