# MCP tool checklist

Complete this checklist before adding or changing an MCP tool in any module.

## Tool design

- [ ] Names a useful job outcome, rather than an endpoint, CRUD operation or database action.
- [ ] Has a module owner and invokes that module's application service.
- [ ] States why an MCP tool is appropriate; documents a deliberate no-tool decision when the behavior is not agent-usable.
- [ ] Does not expose generic CRUD, generic SQL, provider administration or hidden business rules.

## Contract and authorization

- [ ] Defines versioned input and output JSON Schema with examples and validation errors.
- [ ] Requires an explicit `empresa_id` for every command; it never infers a company from the last request or a resource identifier.
- [ ] Declares the required named capability and scope behavior.
- [ ] Declares risk as read-only, internal write, external, financial or irreversible.
- [ ] Authorizes the authenticated actor, membership, capability and scope on every invocation, even if discovery hid the tool.

## Execution evidence

- [ ] Declares correlation-ID propagation and writes an audit event with actor, company, action, resource, outcome and redacted metadata.
- [ ] Defines a caller-supplied idempotency key and replay result for every write.
- [ ] Uses prepare/confirm for external, financial or irreversible work. The confirmation is single-use, short-lived and bound to actor, company, action and payload hash.
- [ ] Records a denial without exposing sensitive data; never logs secrets, prompts, medical content or file content.

## Quality

- [ ] Has a public contract or BDD scenario for its valuable behavior.
- [ ] Has authorization, company-context, audit and idempotency coverage at the appropriate layer.
- [ ] Documents observability signals and any provider boundary used.
