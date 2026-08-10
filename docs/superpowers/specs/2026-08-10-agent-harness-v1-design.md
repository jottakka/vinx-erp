# Agent Harness V1 — Design

## Status

Approved for the first implementation slice on 2026-08-10. This design implements issue #127 while keeping remote MCP hosting, OAuth, WhatsApp transport, OCR storage, `whoAmI`, and durable approval flows in their own issues.

## Goal

Build the internal harness that runs a supervised agent request consistently for future VINX UI and WhatsApp channels. It must use model- and transport-neutral contracts, make tool policy deterministic, and never let a model bypass company scope, capabilities, confirmation, audit, or idempotency requirements.

## Scope boundary

The first slice is an in-process `Vinx.Erp.Foundation.AI` harness. It does not expose an HTTP endpoint, start an MCP server, call a real provider, persist a conversation, upload a file, or execute a domain mutation.

It delivers an executable core with one read-only in-process tool in tests, deterministic model/test doubles, and typed outcomes. This proves the boundary needed before real provider, channel, and MCP-host adapters exist.

## Decisions

### Location

Place the harness in `src/Foundation/Vinx.Erp.Foundation/AI/` under namespace `Vinx.Erp.Foundation.AI`.

`Vinx.Erp.Foundation` already owns identity, capabilities, audit, correlation, idempotency, provider boundaries, and cross-cutting contracts. A separate assembly would add project wiring without a current domain boundary. The Foundation assembly must remain independent from `ApiHost`, `McpHost`, and `WorkerHost`.

### Harness contracts

The core exposes these contracts:

- `AgentRunRequest`: internal actor, server correlation ID, channel, optional explicit company, normalized context items, and task profile.
- `AgentRunOutcome`: one of `result`, `requires_confirmation`, `denied`, `conflict`, `validation_error`, or `needs_clarification`; contains only safe user-facing text, tool name, and opaque identifiers needed by the caller.
- `AgentTaskProfile`: approved task identity and limits. Business code selects a profile, never a raw provider/model name.
- `IAgentModel`: emits either a final assistant message or a typed tool proposal. The initial test double is test-only; production adapters come later.
- `IAgentTool`: exposes a job-oriented descriptor and executes in-process. Its descriptor carries tool name/version, input and output schemas, required capability, risk, explicit-company requirement, and idempotency requirement.
- `IAgentToolCatalog`: resolves only allowlisted tools for the task profile.
- `IAgentPolicy`: decides before invocation. It can allow a read, deny a call, request clarification for missing company, or require confirmation for a write.
- `IAgentRunObserver`: receives safe lifecycle metadata. It receives no raw prompt, token, attachment content, OCR text, or provider payload.

The descriptor follows MCP concepts but is not an MCP transport implementation. It keeps `inputSchema` and `outputSchema` as versioned JSON documents, enabling a later `McpHost` adapter without changing the harness contract.

### Data flow

1. A channel adapter authenticates the caller, resolves the internal actor, creates a server correlation ID, and normalizes message/OCR data into `AgentContextItem` records.
2. The adapter calls `AgentHarness.RunAsync` with an approved `AgentTaskProfile` and `AgentRunRequest`.
3. The harness asks `IAgentModel` for a typed decision.
4. A final decision returns `result` without a tool call.
5. For a tool proposal, the harness validates the catalog/profile allowlist, descriptor, explicit company, policy, and capability context before any invocation.
6. A write returns `requires_confirmation`; the harness never invokes it. Issue #126 later supplies durable approval creation and consumption.
7. An allowed read invokes the in-process tool, returns its typed result to the model once, then returns the model's final response. The task profile caps tool turns.
8. The observer receives redacted state transitions and outcome metadata.

### Company and identity rules

The harness never resolves a user from a prompt, tool argument, or model proposal. `ActorContext` comes from a validated host boundary. A tool marked `RequiresExplicitCompany` cannot run when `CompanyId` is absent; the outcome is `needs_clarification` and the tool is not invoked.

The harness does not implement `whoAmI`. Issue #125 owns membership and capability discovery. The first core receives effective capability names as trusted server-side context from its future host adapter; it never accepts them from user content.

### Tool policy

Tool descriptors are job-oriented, not CRUD. Policy is server-side and has precedence over model text, provider tool annotations, and client-side approval UI.

- Read-only tool: may run only after catalog, company, and capability checks.
- Internal/external/financial/irreversible write: returns `requires_confirmation` before any execution.
- Tool outside profile allowlist or missing capability: returns `denied`.
- Invalid proposal schema: returns `validation_error`.
- Reused idempotency key with incompatible payload: `conflict` is reserved for the later domain/approval adapter.

### Untrusted content

User messages, WhatsApp payloads, OCR text, PDFs, screenshots, tool descriptions received from outside VINX, and model output are untrusted data. They cannot change task profile, capability, risk, company, approval policy, or system instructions.

OCR reaches the harness only as normalized content with source kind and confidence. Quarantine, malware scanning, storage, extraction, and human review remain outside this slice.

### Provider and context collection boundary

`IAgentModel` is protocol-neutral. Follow-up adapters implement OpenAI-compatible and Anthropic Messages protocols behind that interface. Provider/model selection belongs to `AgentTaskProfile` configuration, not business code.

OpenCode with DeepSeek v4 Flash is a future context-collection adapter behind a separate `IAgentContextCollector` boundary. The initial slice accepts normalized context rather than spawning OpenCode from a request path. This avoids unsupervised local process execution, provider credential handling, and prompt logging in the host.

No provider-side persistent conversation, vector store, or file feature is used in V1.

### Observability and audit

The core emits an `AgentRunEvent` only with run ID, actor ID, optional company ID, correlation ID, channel, task profile ID, tool name, risk, phase, and typed outcome. It never includes prompt text, response text, token, attachment content, raw tool arguments, or provider payload.

Hosts later map events to Foundation audit and structured logs. A mutation's durable audit remains the responsibility of its application service and, later, issue #126's approval transaction.

## First implementation slice

The first PR is intentionally small: contracts, deterministic runner, in-process catalog/policy, safe observer boundary, and unit tests. It does not wire DI into a host because no authenticated agent endpoint exists yet.

Acceptance cases:

1. A final model reply returns `result` without resolving or invoking a tool.
2. A read proposal with an explicit company, profile allowlist, and capability invokes exactly one in-process tool and returns `result`.
3. A proposal for a company-scoped tool without company returns `needs_clarification` and does not invoke the tool.
4. A write proposal returns `requires_confirmation` and does not invoke the tool.
5. A tool outside the profile allowlist or without capability returns `denied` and does not invoke the tool.
6. The observer receives safe state events without message/OCR/provider contents.
7. A model that exceeds the profile's tool-turn limit returns `validation_error` and does not invoke another tool.

## Delivery sequence

1. PR 1: Foundation AI harness core and unit tests.
2. PR 2: approved task profile configuration and real OpenAI-compatible/Anthropic adapters, using external-boundary test doubles.
3. PR 3: `whoAmI`, effective identity/capability adapter, durable approval adapter, and one Paguai read/draft vertical slice.
4. PR 4: authenticated API/UI harness adapter and WhatsApp channel adapter with its own webhook/outbox work.
5. PR 5: remote MCP host, OAuth, Streamable HTTP, tool discovery, and compatibility checks for Claude/GPT clients.

The remote MCP host is deliberately later, but every internal tool contract is MCP-compatible from PR 1.

## Verification

The first PR uses xUnit unit tests with test-only deterministic model, tool, catalog, policy, and observer implementations. Tests follow red-green-refactor and assert outcomes and non-invocation behavior rather than mocks alone.

Focused validation runs the Foundation unit test project. Before merge, run `./scripts/verify-public.sh`, `git diff --check`, a public code-format check, and an independent review. No builder opens, lists, or runs `quality/private`.
