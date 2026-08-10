# Agent Harness V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Deliver deterministic in-process Agent Harness core in new AI module. It processes trusted execution context, normalized user request, MCP-compatible internal tool contracts. It exposes no host, provider, mutation, or remote MCP endpoint.

**Architecture:** Vinx.Erp.AI references Foundation only for ActorContext. AgentHarness receives trusted AgentExecutionContext separately from untrusted AgentRunRequest; it asks protocol-neutral model for final reply or one tool call at a time, applies deterministic policy, invokes only allowed read tools, emits safe lifecycle events. API, WhatsApp, provider, approval, OCR, remote MCP adapters stay outside this plan.

**Tech Stack:** .NET 10, C# nullable enabled, xUnit 2.9.3, central package versions, System.Text.Json, Foundation project, public dotnet gates.

## Global Constraints

- Work on branch codex/agentic-v1-harness; never write main.
- Keep first PR at or below 25 changed implementation/documentation files and 800 manual lines; generated lockfiles count as files.
- Estimated first PR: 22 implementation/documentation files plus this plan/spec, and 550–700 manual lines. Split provider, host, channel, OCR, approval, and remote MCP work into later PRs.
- Create src/Modules/AI/Vinx.Erp.AI. It may reference Vinx.Erp.Foundation. Foundation, ApiHost, McpHost, WorkerHost, database schema, migrations, and provider configuration stay unchanged.
- Use namespace Vinx.Erp.AI and subnamespaces AgentHarness, Models, Tools.
- Add no NuGet package and no provider SDK.
- Model, prompt, OCR item, WhatsApp item, tool argument never supplies actor, company, capability, correlation ID, task profile, risk, allowlist, or approval policy.
- Store no prompt, response, token, attachment contents, OCR text, raw tool arguments, or provider payload in logs, observer events, fixtures, or Markdown.
- Tool descriptor is job-oriented and MCP-compatible: name, version, JSON input/output schemas, required capability, risk, explicit-company requirement, idempotency requirement, MCP annotation hints.
- Policy is server-side. Any non-read-only risk returns requires_confirmation before invocation. No mutating test tool is registered or invoked.
- Tool requiring explicit company returns needs_clarification when AgentExecutionContext.CompanyId is null.
- Caller cancellation propagates. Internal profile timeout returns validation_error without another tool invocation.
- Documentation Portuguese. BDD/verification cases English. Never access quality/private.

---

## File Structure

| Path | Responsibility |
| --- | --- |
| src/Modules/AI/Vinx.Erp.AI/Vinx.Erp.AI.csproj | AI module references Foundation only. |
| src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentContracts.cs | Trusted/untrusted execution contracts, profile, outcomes, channel/context enums. |
| src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentRunObservation.cs | Safe lifecycle event and observer contract. |
| src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentHarness.cs | Deterministic model/tool loop, timeout, outcome mapping. |
| src/Modules/AI/Vinx.Erp.AI/Models/AgentModelContracts.cs | Provider-neutral model request and turn contracts. |
| src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolContracts.cs | Tool descriptor, call, invocation, result, tool interface. |
| src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolRegistry.cs | Immutable registered-tool lookup. |
| src/Modules/AI/Vinx.Erp.AI/Tools/AgentRunPolicy.cs | Allow, deny, clarification, confirmation decision. |
| tests/public/Vinx.Erp.AI.UnitTests | xUnit tests and test-only doubles. |
| tests/public/Vinx.Erp.ArchitectureTests/AiDependencyTests.cs | AI module host-dependency boundary. |
| docs/modules/ai.md | Durable module contract. |
| docs/adr/0005-agent-harness-core.md | Internal MCP-compatible core decision. |
| docs/verification/127-agent-harness-test-cases.md | Issue #127 to public-test mapping. |

### Task 1: Create AI module and immutable contracts

**Files:**

- Create: src/Modules/AI/Vinx.Erp.AI/Vinx.Erp.AI.csproj
- Create: src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentContracts.cs
- Create: src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentRunObservation.cs
- Create: src/Modules/AI/Vinx.Erp.AI/Models/AgentModelContracts.cs
- Create: src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolContracts.cs
- Create: tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj
- Create: tests/public/Vinx.Erp.AI.UnitTests/AgentContractsTests.cs
- Modify: Vinx.Erp.sln
- Generate: src/Modules/AI/Vinx.Erp.AI/packages.lock.json
- Generate: tests/public/Vinx.Erp.AI.UnitTests/packages.lock.json

**Interfaces:**

- Consumes: Vinx.Erp.Foundation.Identity.ActorContext.
- Produces: AgentExecutionContext, AgentRunRequest, AgentTaskProfile, AgentRunOutcome, AgentRunOutcomeKind, AgentRunPhase, AgentChannel, AgentContextItem, IAgentRunObserver, AgentRunEvent, IAgentModel, AgentModelRequest, AgentModelTurn, IAgentTool, AgentToolDescriptor, AgentToolCall, AgentToolInvocation, AgentToolResult, AgentToolValidationResult, AgentToolRisk, AgentMcpAnnotations.

- [ ] **Step 1: Create minimal project references**

Create module project with Foundation reference only. Create test project with AI and Foundation references. Add both projects to solution. Use existing central xUnit/Test SDK entries.

~~~xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../../Foundation/Vinx.Erp.Foundation/Vinx.Erp.Foundation.csproj" />
  </ItemGroup>
</Project>
~~~

Restore once without locked mode to create new lockfiles, then restore solution with locked mode.

- [ ] **Step 2: Write failing contract tests**

Production changes that must make tests fail: removing blank-message validation, accepting zero tool-turn profile, or allowing model/user input to replace trusted company.

~~~csharp
[Fact]
public void Run_request_rejects_blank_user_message()
{
    Assert.Throws<ArgumentException>(() => new AgentRunRequest(" ", []));
}

[Fact]
public void Task_profile_rejects_zero_tool_turns()
{
    Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskProfile(
        "support.read", 0, TimeSpan.FromSeconds(10), new HashSet<string>()));
}

[Fact]
public void Execution_context_keeps_server_supplied_company()
{
    var companyId = Guid.NewGuid();

    Assert.Equal(companyId, CreateContext(companyId).CompanyId);
}
~~~

- [ ] **Step 3: Run test and verify RED**

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentContractsTests
~~~

Expected: compile failure because contract types do not exist.

- [ ] **Step 4: Implement minimal contracts**

Use exact API shape:

~~~csharp
public sealed record AgentExecutionContext(
    ActorContext Actor,
    Guid? CompanyId,
    AgentChannel Channel,
    IReadOnlySet<string> EffectiveCapabilities,
    AgentTaskProfile Profile);

public sealed record AgentRunRequest(
    string UserMessage,
    IReadOnlyList<AgentContextItem> ExternalContext);

public sealed record AgentTaskProfile(
    string Id,
    int MaxToolTurns,
    TimeSpan Timeout,
    IReadOnlySet<string> AllowedTools);

public enum AgentRunOutcomeKind
{
    Result,
    RequiresConfirmation,
    Denied,
    Conflict,
    ValidationError,
    NeedsClarification
}
~~~

Use this outcome shape so Task 3 can return the fields asserted by its tests:

~~~csharp
public sealed record AgentRunOutcome(
    AgentRunOutcomeKind Kind,
    string SafeCode,
    string? AssistantMessage,
    string? ToolName);
~~~

Reject null/blank IDs, blank user text, empty tool names, unsupported enums, non-positive turn limits, and non-positive timeouts. Use JsonElement values cloned from source documents; contracts must not retain disposable JsonDocument instances.

AgentModelRequest contains only profile, normalized request, allowed descriptors, and prior tool results. It must not contain ActorContext, company, capabilities, correlation ID, or observer event data.

AgentToolDescriptor contains Name, Version, InputSchema, OutputSchema, RequiredCapability, Risk, RequiresExplicitCompany, RequiresIdempotencyKey, and MCP annotations. IAgentTool.Validate returns typed validation result. IAgentTool.InvokeAsync receives AgentToolInvocation, whose company comes only from AgentExecutionContext.

AgentRunEvent contains only run ID, actor ID, optional company ID, correlation ID, channel, profile ID, optional tool name/risk, phase, and optional outcome. It has no text/content property.

Use this test helper shape in AgentContractsTests; it creates only trusted server-side values:

~~~csharp
private static AgentExecutionContext CreateContext(Guid? companyId) => new(
    new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
    companyId,
    AgentChannel.Web,
    new HashSet<string>(StringComparer.Ordinal) { "paguai.read" },
    new AgentTaskProfile(
        "support.read",
        1,
        TimeSpan.FromSeconds(1),
        new HashSet<string>(StringComparer.Ordinal) { "paguai.read" }));
~~~

- [ ] **Step 5: Run test and verify GREEN**

~~~bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentContractsTests
dotnet build tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
~~~

Expected: all focused tests pass, build has no warnings.

- [ ] **Step 6: Commit**

~~~bash
git add Vinx.Erp.sln src/Modules/AI/Vinx.Erp.AI tests/public/Vinx.Erp.AI.UnitTests
git commit -m "feat: add agent harness contracts"
~~~

### Task 2: Add immutable tool registry and server-side policy

**Files:**

- Create: src/Modules/AI/Vinx.Erp.AI/Tools/AgentToolRegistry.cs
- Create: src/Modules/AI/Vinx.Erp.AI/Tools/AgentRunPolicy.cs
- Create: tests/public/Vinx.Erp.AI.UnitTests/AgentToolRegistryTests.cs
- Create: tests/public/Vinx.Erp.AI.UnitTests/Fixtures/AgentHarnessDoubles.cs

**Interfaces:**

- Consumes: Task 1 AgentExecutionContext, AgentToolDescriptor, IAgentTool, AgentToolRisk, AgentRunOutcomeKind.
- Produces: AgentToolRegistry.TryGet, AgentRunPolicy.Evaluate, AgentPolicyDecision.

- [ ] **Step 1: Write failing registry/policy tests**

Production changes that must make tests fail: accepting duplicate tool names, invoking tool outside profile allowlist, ignoring missing capability, accepting absent company for company-scoped tool, or allowing write without confirmation.

~~~csharp
[Fact]
public void Registry_rejects_duplicate_tool_names()
{
    var duplicate = new RecordingAgentTool("paguai.read");

    Assert.Throws<ArgumentException>(() => new AgentToolRegistry([duplicate, duplicate]));
}

[Theory]
[InlineData(AgentToolRisk.InternalWrite, AgentRunOutcomeKind.RequiresConfirmation)]
[InlineData(AgentToolRisk.Financial, AgentRunOutcomeKind.RequiresConfirmation)]
public void Policy_requires_confirmation_before_any_write(
    AgentToolRisk risk,
    AgentRunOutcomeKind expected)
{
    var decision = policy.Evaluate(CreateContext(companyId), CreateDescriptor(risk));

    Assert.Equal(expected, decision.OutcomeKind);
}
~~~

Add tests for needs_clarification when company missing and denied for missing capability or disallowed tool.

- [ ] **Step 2: Run test and verify RED**

~~~bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentToolRegistryTests
~~~

Expected: compile failure because registry/policy do not exist.

- [ ] **Step 3: Implement smallest registry/policy**

AgentToolRegistry builds a case-sensitive immutable map keyed by descriptor name and rejects duplicate names at construction. It has no mutating registration API.

AgentRunPolicy.Evaluate applies checks in exact order:

1. Profile allowlist contains descriptor name; otherwise Denied code tool_not_allowed.
2. Required explicit company exists in AgentExecutionContext; otherwise NeedsClarification code company_context_required.
3. Trusted effective capabilities contain RequiredCapability; otherwise Denied code capability_denied.
4. Risk is not ReadOnly; otherwise RequiresConfirmation code confirmation_required.
5. Return Result code tool_allowed.

Policy receives no AgentRunRequest, OCR text, model message, or raw tool arguments.

- [ ] **Step 4: Run test and verify GREEN**

~~~bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentToolRegistryTests
~~~

Expected: all focused tests pass with no warnings.

- [ ] **Step 5: Commit**

~~~bash
git add src/Modules/AI/Vinx.Erp.AI/Tools tests/public/Vinx.Erp.AI.UnitTests/AgentToolRegistryTests.cs tests/public/Vinx.Erp.AI.UnitTests/Fixtures/AgentHarnessDoubles.cs
git commit -m "feat: enforce agent tool policy"
~~~

### Task 3: Implement deterministic harness loop and safe observation

**Files:**

- Create: src/Modules/AI/Vinx.Erp.AI/AgentHarness/AgentHarness.cs
- Create: tests/public/Vinx.Erp.AI.UnitTests/AgentHarnessTests.cs
- Modify: tests/public/Vinx.Erp.AI.UnitTests/Fixtures/AgentHarnessDoubles.cs

**Interfaces:**

- Consumes: IAgentModel, AgentToolRegistry, AgentRunPolicy, IAgentRunObserver, all Task 1 contracts.
- Produces: AgentHarness.RunAsync(AgentExecutionContext context, AgentRunRequest request, CancellationToken cancellationToken).

- [ ] **Step 1: Write failing harness tests**

Production changes that must make tests fail: tool invoked after policy denies it, model company argument replacing trusted context, raw user content recorded, second tool past limit, or tool invoked after timeout/caller cancellation.

~~~csharp
[Fact]
public async Task Run_returns_final_reply_without_invoking_tool()
{
    var model = ScriptedAgentModel.Final("Olá");
    var tool = new RecordingAgentTool("paguai.read");
    var harness = CreateHarness(model, tool);

    var outcome = await harness.RunAsync(CreateContext(Guid.NewGuid()), CreateRequest("status"), CancellationToken.None);

    Assert.Equal(AgentRunOutcomeKind.Result, outcome.Kind);
    Assert.Equal("Olá", outcome.AssistantMessage);
    Assert.Empty(tool.Invocations);
}

[Fact]
public async Task Run_returns_confirmation_without_invoking_write_tool()
{
    var writeTool = new RecordingAgentTool("paguai.draft.create", AgentToolRisk.InternalWrite);
    var harness = CreateHarness(ScriptedAgentModel.Tool("paguai.draft.create", "{}"), writeTool);

    var outcome = await harness.RunAsync(CreateContext(Guid.NewGuid()), CreateRequest("crie"), CancellationToken.None);

    Assert.Equal(AgentRunOutcomeKind.RequiresConfirmation, outcome.Kind);
    Assert.Empty(writeTool.Invocations);
}
~~~

Add test cases for allowed read then final reply, missing company, missing capability, unknown tool, invalid arguments, model-proposed empresa_id ignored, injection text ignored by policy, timeout, caller cancellation, turn cap, safe observer shape, and concurrent actor/company/correlation isolation.

- [ ] **Step 2: Run test and verify RED**

~~~bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentHarnessTests
~~~

Expected: compile failure because AgentHarness does not exist.

- [ ] **Step 3: Implement minimal deterministic loop**

1. Create linked cancellation source from caller token and context.Profile.Timeout.
2. Emit Received event.
3. Build AgentModelRequest from untrusted request, allowed descriptors, prior tool results only.
4. Ask model for one AgentModelTurn.
5. Final turn emits Completed and returns AgentRunOutcome.Result.
6. Tool turn resolves registry. Unknown tool returns Denied without invocation.
7. IAgentTool.Validate invalid arguments returns ValidationError without invocation.
8. Policy result other than Result returns typed outcome without invocation.
9. Invoke only allowed read tool with AgentToolInvocation built from trusted AgentExecutionContext; append result and increment turn count.
10. Before another tool invocation, exhausted MaxToolTurns returns ValidationError code tool_turn_limit_exceeded.
11. Internal timeout returns ValidationError code agent_timeout. Caller cancellation rethrows.

Observer receives fixed phase/outcome metadata only. It never receives AgentRunRequest.UserMessage, AgentContextItem.Content, model text, JsonElement arguments, or AgentToolResult payload.

- [ ] **Step 4: Run test and verify GREEN**

~~~bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter FullyQualifiedName~AgentHarnessTests
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
~~~

Expected: all harness and AI unit tests pass with no warnings.

- [ ] **Step 5: Commit**

~~~bash
git add src/Modules/AI/Vinx.Erp.AI/AgentHarness tests/public/Vinx.Erp.AI.UnitTests
git commit -m "feat: run supervised agent harness"
~~~

### Task 4: Lock module boundary and delivery evidence

**Files:**

- Create: tests/public/Vinx.Erp.ArchitectureTests/AiDependencyTests.cs
- Modify: tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj
- Modify: tests/public/Vinx.Erp.ArchitectureTests/ModuleContractDocumentationTests.cs
- Create: docs/modules/ai.md
- Create: docs/adr/0005-agent-harness-core.md
- Create: docs/verification/127-agent-harness-test-cases.md

**Interfaces:**

- Consumes: AI assembly and module documentation headings.
- Produces: architecture regression proof, module ownership contract, durable ADR, issue-to-test matrix.

- [ ] **Step 1: Write failing architecture/doc tests**

Add Vinx.Erp.AI project reference to architecture test project. Add docs/modules/ai.md to ModuleContractDocumentationTests theory data. Write:

~~~csharp
[Fact]
public void Ai_module_does_not_depend_on_transport_hosts()
{
    var forbidden = new HashSet<string>(StringComparer.Ordinal)
    {
        "Vinx.Erp.ApiHost",
        "Vinx.Erp.McpHost",
        "Vinx.Erp.WorkerHost"
    };

    var dependencies = typeof(AgentHarness).Assembly.GetReferencedAssemblies()
        .Select(assembly => assembly.Name)
        .ToHashSet(StringComparer.Ordinal);

    Assert.Empty(dependencies.Intersect(forbidden));
}
~~~

- [ ] **Step 2: Run test and verify RED**

~~~bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore --filter FullyQualifiedName~ModuleContractDocumentationTests
~~~

Expected: module-contract documentation test fails because docs/modules/ai.md does not exist.

- [ ] **Step 3: Create durable documentation**

docs/modules/ai.md contains all standard headings. It states AI owns harness, task profiles, provider adapters, safe run events, future agent channel adapters. Foundation owns actor identity, capabilities, audit, correlation, idempotency. Commands require trusted company; model/user content never establishes scope. Contract version 1.0. First slice has no persisted AI data, deletion workflow, or migration.

docs/adr/0005-agent-harness-core.md records separate AI module, MCP-compatible internal tool contracts, no remote MCP host/provider SDK in this slice.

docs/verification/127-agent-harness-test-cases.md maps issue #127 BDD categories to exact public tests: final reply, read tool, missing company, denied capability/allowlist, confirmation barrier, model company override, untrusted injection, timeout/cancel, safe observation, concurrency isolation. State deferred #125, #126, #28, #48, #52, #40.

- [ ] **Step 4: Run test and verify GREEN**

~~~bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
~~~

Expected: both projects pass with no warnings.

- [ ] **Step 5: Commit**

~~~bash
git add tests/public/Vinx.Erp.ArchitectureTests docs/modules/ai.md docs/adr/0005-agent-harness-core.md docs/verification/127-agent-harness-test-cases.md
git commit -m "docs: record agent harness boundary"
~~~

### Task 5: Final verification and review gate

**Files:**

- Verify: all files changed by Tasks 1–4.

**Interfaces:**

- Consumes: harness tests, architecture tests, module docs, ADR, verification matrix.
- Produces: public-gate evidence and independent review package.

- [ ] **Step 1: Run focused tests**

~~~bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
~~~

- [ ] **Step 2: Run public release gate**

~~~bash
./scripts/verify-public.sh
git diff --check
~~~

Expected: both commands exit 0. Public gate may use disposable PostgreSQL only; never invoke private suite.

- [ ] **Step 3: Independently review complete branch diff**

Generate review material from merge base and dispatch independent reviewer. Fix Critical/Important findings using subagent-driven review loop before merge decision.

~~~bash
git fetch origin main
git merge-base origin/main HEAD
git diff --check origin/main...HEAD
~~~

- [ ] **Step 4: Commit review fixes and prepare PR evidence**

Use focused commit only when review changes are needed. PR evidence lists focused tests, public gate result, no remote MCP/provider/host/UI/WhatsApp implementation, deferred #125, #126, #28, #48, #52, #40.
