# MCP contract policy

MCP is a first-class transport. A module's MCP tool calls the same application service and enforces the same company, authorization, business, audit and observability contracts as the HTTP API.

Each tool manifest documents: useful job intent; module owner; input/output JSON Schema; explicit `empresa_id`; required capability and scope; risk; correlation and audit behavior; idempotency behavior for writes; and the prepare/confirm flow when applicable. Tool discovery is a convenience only; invocation always checks the authenticated actor.

No MCP tool may be a generic CRUD or SQL interface, infer company context for a command, expose provider credentials, or bypass a module's use case. Read, summarize and draft are the initial AI-facing outcomes. Financial, external and irreversible actions require a human confirmation bound to actor, company, action and payload hash.

The canonical review checklist is [MCP tool checklist](../../modules/mcp-tool-checklist.md).
