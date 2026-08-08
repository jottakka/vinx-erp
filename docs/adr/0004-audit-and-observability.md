# ADR 0004: Durable audit and portable observability are Foundation contracts

## Status

Accepted

## Context

The ERP handles approvals, employee absence status, files and supervised AI work. Operational logs alone cannot prove who attempted or completed a business action, and inconsistent telemetry makes cross-host diagnosis hard.

## Decision

Foundation provides durable append-only audit events and an observability contract based on structured logs, OpenTelemetry traces and metrics, health checks, redaction and correlation IDs. Audit records actor, company, source, action, resource, outcome, reason, correlation ID and redacted metadata.

Successful business actions audit in their business transaction. Denied attempts are auditable. Application roles cannot update or delete audit events. Correlation IDs cross web, API, MCP, worker, outbox and provider calls.

Do not log passwords, tokens, raw prompts, medical content or file content.

## Consequences

- Audit is reliable business evidence, distinct from operational diagnostics.
- A portable exporter configuration avoids coupling the application to one observability vendor.
- Modules must include audit and useful telemetry in their definition of done.
- Redaction requires deliberate metadata design; debugging cannot rely on sensitive payload capture.
