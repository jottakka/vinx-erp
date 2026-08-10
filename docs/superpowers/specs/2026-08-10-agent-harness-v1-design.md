# Agent Harness V1 — Design

## Status

Approved for the first implementation slice on 2026-08-10 and reviewed with Sol. This design implements issue #127 while keeping remote MCP hosting, OAuth, WhatsApp transport, OCR storage, `whoAmI`, and durable approval flows in their own issues.

## Goal

Build the internal harness that runs a supervised agent request consistently for future VINX UI and WhatsApp channels. It must use model- and transport-neutral contracts, make tool policy deterministic, and never let a model bypass company scope, capabilities, confirmation, audit, or idempotency requirements.

## Scope boundary

The first slice is an in-process `Vinx.Erp.AI` harness. It does not expose an HTTP endpoint, start an MCP server, call a real provider, persist a conversation, upload a file, or execute a domain mutation.

It delivers an executable core with one read-only in-process tool in tests, deterministic model/test doubles, and typed outcomes. This proves the boundary needed before real provider, channel, and MCP-host adapters exist.

## Decisions

### Location

Place the harness in `src/Modules/AI/Vinx.Erp.AI/` under namespace `Vinx.Erp.AI`. It references `Vinx.Erp.Foundation` for trusted actor and cross-cutting contracts.

AI is a declared module in the modular-monolith architecture. Keeping the harness in its own assembly prevents Foundation from owning AI lifecycle logic while preserving Foundation's independence from `ApiHost`, `McpHost`, and `WorkerHost`. Hosts remain future adapters and do not become harness dependencies.

### Harness contracts

The core exposes these contracts:

- `AgentExecutionContext`: trusted server-side actor, server correlation ID, channel, optional explicit company, effective capabilities, and approved task profile.
- `AgentRunRequest`: untrusted normalized user message and external context items. It never supplies actor, company, capability, correlation, profile, or policy.
- `AgentRunOutcome`: one of `result`, `requires_confirmation`, `denied`, `conflict`, `validation_error`, or `needs_clarification`; contains only safe user-facing text, tool name, and opaque identifiers needed by the caller.
- `AgentTaskProfile`: approved task identity and limits, including a timeout supported by portable cancellation. Business code selects a profile, never a raw provider/model name.
- `IAgentModel`: emits either a final assistant message or a typed tool proposal. Its request has normalized user/context content, approved tool descriptors, and prior results only; it never receives `AgentExecutionContext` or `AgentTaskProfile`. The initial test double is test-only; production adapters come later.
- `IAgentTool`: exposes a job-oriented descriptor and executes in-process. Its descriptor carries tool name/version, input and output schemas, required capability, risk, explicit-company requirement, and idempotency requirement.
- `IAgentToolRegistry`: immutable catalog that resolves only allowlisted tools for the task profile.
- `IAgentRunPolicy`: decides before invocation. It holds maximum tool turns, timeout, allowlist, and write prohibition. It can allow a read, deny a call, request clarification for missing company, or require confirmation for a write.
- `IAgentRunObserver`: receives safe lifecycle metadata. It receives no raw prompt, token, attachment content, OCR text, or provider payload.

The descriptor follows MCP concepts but is not an MCP transport implementation. It keeps `inputSchema` and `outputSchema` as versioned JSON documents, enabling a later `McpHost` adapter without changing the harness contract. In this slice, the resolved tool owns semantic argument validation; a generic JSON Schema engine belongs to the remote MCP adapter work.

### Data flow

1. A channel adapter authenticates the caller, resolves the internal actor, creates a server correlation ID, computes effective capabilities, and normalizes message/OCR data into `AgentContextItem` records.
2. The adapter calls `AgentHarness.RunAsync` with an `AgentExecutionContext` and `AgentRunRequest`.
3. The harness asks `IAgentModel` for a typed decision.
4. A final decision returns `result` without a tool call.
5. For a tool proposal, the harness resolves the catalog/descriptor, evaluates profile allowlist, explicit company, capability, and risk policy, then performs semantic tool validation only for an allowed read. No denied or confirmation-required proposal reaches tool validation or invocation.
6. A write returns `requires_confirmation`; the harness never invokes it. Issue #126 later supplies durable approval creation and consumption.
7. An allowed read invokes the in-process tool and returns its typed result to the model. The harness repeats only while the policy's tool-turn cap has not been reached.
8. The observer receives redacted state transitions and outcome metadata.

### Company and identity rules

The harness never resolves a user, company, correlation ID, capability, or profile from a prompt, tool argument, or model proposal. `ActorContext` and the optional company come only from `AgentExecutionContext`. A tool marked `RequiresExplicitCompany` cannot run when the trusted company is absent; the outcome is `needs_clarification` and the tool is not invoked.

`AgentToolCall` removes `empresa_id`, `empresaId`, `company_id`, and `companyId` aliases case-insensitively at every JSON depth before an in-process tool sees its arguments. An external MCP adapter has a different boundary: it accepts an explicit `empresa_id`, authorizes it, and then constructs `AgentExecutionContext.CompanyId` before it creates the internal tool call. It never forwards that value as a model-controlled argument.

The harness does not implement `whoAmI`. Issue #125 owns membership and capability discovery. The first core receives effective capability names as trusted server-side context from its future host adapter; it never accepts them from user content.

### Tool policy

Tool descriptors are job-oriented, not CRUD. Policy is server-side and has precedence over model text, provider tool annotations, and client-side approval UI.

- Read-only tool: may run only after catalog, company, and capability checks.
- Internal/external/financial/irreversible write: returns `requires_confirmation` before any execution.
- Tool outside profile allowlist or missing capability: returns `denied`.
- Invalid proposal schema: returns `validation_error`.
- Reused idempotency key with incompatible payload: `conflict` is reserved for the later domain/approval adapter.

Unexpected observer, model, and tool failures propagate to the host; they are not mislabeled as user validation errors. A tool result with `IsSuccess == false` is returned to the model, consumes a tool turn, and is never retried automatically. Caller cancellation propagates even when it races with the internal timeout.

### Untrusted content

User messages, WhatsApp payloads, OCR text, PDFs, screenshots, tool descriptions received from outside VINX, and model output are untrusted data. They cannot change task profile, capability, risk, company, approval policy, or system instructions.

OCR reaches the harness only as normalized content with source kind and confidence. Quarantine, malware scanning, storage, extraction, and human review remain outside this slice.

### Provider and context collection boundary

`IAgentModel` is protocol-neutral. Follow-up adapters implement OpenAI-compatible and Anthropic Messages protocols behind that interface. Provider/model selection belongs to server-side `AgentTaskProfile` configuration outside `AgentModelRequest`, not business code.

OpenCode with DeepSeek v4 Flash is a future context-collection adapter behind a separate `IAgentContextCollector` boundary. The initial slice accepts normalized context rather than spawning OpenCode from a request path. This avoids unsupervised local process execution, provider credential handling, and prompt logging in the host.

No provider-side persistent conversation, vector store, or file feature is used in V1.

### Observability and audit

The core emits an `AgentRunEvent` only with run ID, actor ID, optional company ID, correlation ID, channel, task profile ID, tool name, risk, phase, and typed outcome. It never includes prompt text, response text, token, attachment content, raw tool arguments, or provider payload.

Hosts later map events to Foundation audit and structured logs. A mutation's durable audit remains the responsibility of its application service and, later, issue #126's approval transaction.

## First implementation slice

The initial delivery is a small stack: contracts, policy, trusted-scope guard, deterministic runner, then architecture/documentation evidence. It does not wire DI into a host because no authenticated agent endpoint exists yet.

Acceptance cases:

1. A final model reply returns `result` without resolving or invoking a tool.
2. A read proposal with an explicit company, profile allowlist, and capability invokes exactly one in-process tool and returns `result`.
3. A proposal for a company-scoped tool without company returns `needs_clarification` and does not invoke the tool.
4. A write proposal returns `requires_confirmation` and does not invoke the tool.
5. A tool outside the profile allowlist or without capability returns `denied` and does not invoke the tool.
6. A company ID proposed by the model cannot replace the trusted company in `AgentExecutionContext`.
7. Instructions embedded in untrusted OCR/context cannot change profile, policy, allowlist, capability, or company.
8. The observer receives safe state events without message/OCR/provider contents.
9. A model that exceeds the profile's tool-turn limit or timeout returns `validation_error` and does not invoke another tool; caller cancellation propagates without invoking another tool.
10. Concurrent runs do not mix actor, company, or correlation data.

## Delivery sequence

1. PR 1: AI module contracts and unit tests.
2. PR 2: immutable registry and server-side policy.
3. PR 3: trusted-scope and timeout guard.
4. PR 4: deterministic runner and unit tests.
5. PR 5: architecture boundary and durable delivery evidence.
6. PR 6: approved task profile configuration and real OpenAI-compatible/Anthropic adapters, using external-boundary test doubles.
7. PR 7: `whoAmI`, effective identity/capability adapter, durable approval adapter, and one Paguai read/draft vertical slice.
8. PR 8: authenticated API/UI harness adapter and WhatsApp channel adapter with its own webhook/outbox work.
9. PR 9: remote MCP host, OAuth, Streamable HTTP, tool discovery, and compatibility checks for Claude/GPT clients.

The remote MCP host is deliberately later, but every internal tool contract is MCP-compatible from PR 1.

## Verification

The first PR uses xUnit unit tests with test-only deterministic model, tool, registry, policy, and observer implementations. Tests follow red-green-refactor and assert outcomes and non-invocation behavior rather than mocks alone.

Focused validation runs the AI unit test project. Before merge, run `./scripts/verify-public.sh`, `git diff --check`, a public code-format check, and an independent review. No builder opens, lists, or runs `quality/private`.
