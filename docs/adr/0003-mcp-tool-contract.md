# ADR 0003: MCP tools are authorized job contracts, not API mirrors

## Status

Accepted

## Context

MCP is a first-class transport for every module. An agent must perform useful supervised work without gaining a generic database or CRUD surface, and without bypassing multi-company authorization.

## Decision

Expose only job-oriented MCP tools that call the same module application services as HTTP. Each tool declares a useful human outcome, input and output JSON Schema, explicit `empresa_id`, required capability, risk level, audit policy, correlation behavior, and write idempotency behavior.

MCP discovery may hide unavailable tools, but invocation always authorizes the authenticated actor and company membership. A write that is financial, external or irreversible uses prepare/confirm. The single-use, short-lived confirmation is bound to actor, company, action and payload hash.

Generic CRUD tools, generic SQL tools and tools that infer company context for a command are prohibited.

## Consequences

- Tools remain concise and useful to agents rather than copying endpoint shapes.
- HTTP and MCP enforce the same business policy and are independently auditable.
- Confirmation adds a small interaction cost to high-risk work and prevents a prepared action being reused by another actor, company or payload.
- Each module must make an explicit MCP decision for new behavior, including a documented reason when no tool is appropriate.
