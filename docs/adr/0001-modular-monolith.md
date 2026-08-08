# ADR 0001: Modular monolith with three transport hosts

## Status

Accepted

## Context

The ERP serves a small group of related companies and will initially be maintained by one experienced C# developer. It needs independent module boundaries, while authorization, audit, data consistency and operational work must remain simple to evolve and deploy.

## Decision

Use one .NET modular monolith and one PostgreSQL database with logical module schemas. `ApiHost`, `McpHost` and `WorkerHost` are thin transport hosts that invoke the same module application services. Modules own their business rules and data; Foundation supplies cross-cutting contracts only.

Do not introduce microservices, a generic repository, a mandatory mediator, or shared business services during groundwork. Introduce a boundary only when an active use case needs an alternate provider or independently volatile dependency.

## Consequences

- A module can expose HTTP, worker and MCP entry points without duplicating business rules.
- A single deploy and transaction boundary accelerate the pilot.
- Module coupling is controlled by documented contracts and review, not by a network boundary.
- A future extraction must be justified by measured operational or ownership pressure, not anticipated scale.
