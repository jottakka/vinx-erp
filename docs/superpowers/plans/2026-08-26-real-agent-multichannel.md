# Real Multichannel Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver issue #335 as one provider-agnostic OpenAI-compatible Agent shared by Web, WhatsApp, and Telegram, including safe OCR and an explicitly confirmed Pagaí draft proposal.

**Architecture:** Preserve `OpenAiCompatibleChatClient` as the only provider protocol adapter. Move application execution below transports, claim one durable monotonic ledger record before any external call, and keep channel admission/delivery in Notifications. Persist only safe typed Pagaí proposals; a dedicated Pagaí service owns the entire confirmation transaction.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core/Npgsql, Microsoft.Extensions.AI/OpenAI SDK, Wolverine/PostgreSQL outbox, React 19/TypeScript/Vite/MUI/Zod/Vitest, Testcontainers, Node launcher tests.

## Global Constraints

- One branch, one issue (#335), and one PR. PR #332 and merged Telegram history remain untouched.
- The core supports only the OpenAI-compatible protocol now; endpoint/model/host/limits are server-side profile data. Do not add a provider façade, plugin system, protocol registry, Anthropic, Responses API, or unused abstraction.
- OpenCode Go is only the first real profile and smoke target: base `https://opencode.ai/zen/go/v1`, model `deepseek-v4-flash-vision-exp`.
- The only runtime key source is `VINX_AGENT_PROVIDER_API_KEY` in ApiHost/WorkerHost. Never bind a provider key from appsettings, ordinary `IConfiguration`, command-line arguments, Vite, DB, or files in the Harness.
- Automated tests always use fakes and no external network. Real verification is one text request and one vision request with synthetic content after final review.
- Never send real WhatsApp/Telegram messages during delivery. Never inspect or expose a real recipient address.
- TDD is mandatory: write one behavioral test, observe the expected failure, implement the minimum, and rerun only the focused slice. Do not run full Foundation Integration or `verify-public.sh` during each task.
- Preserve authorization, company context, RLS, antiforgery, audit, structured redacted logs, cancellation, no retry, and existing channel ambiguity barriers.
- No prompt, OCR text, raw provider response, bytes, token, phone, chat ID, username, or external URL is persisted in new Agent/Pagaí records or emitted as operational evidence.
- No new package unless the BCL/current SDK cannot meet an observed requirement. No refactors or docs outside the files listed below.
- Review once after the complete branch, then one consolidated correction wave. No repeated review/test loops.

## File Structure

- `src/Modules/Vinx.Erp.AI/`: normalized command, execution service/result, narrow lifecycle hook, provider credential/transport, strict Pagaí output parser.
- `src/Foundation/Vinx.Erp.Foundation/`: ledger/proposal entities, existing `FileRecord`/`PaguaiRequest` evolution, EF mappings and migrations owned by the shared database.
- `src/Modules/Vinx.Erp.Paguai/`: safe proposal read/reject/confirm behavior and pure draft construction reused inside one transaction.
- `src/Modules/Vinx.Erp.Notifications/`: opaque inbound Agent envelopes, channel materialization/translation, and result-to-outbox mapping.
- `src/Hosts/Vinx.Erp.ApiHost/`: thin Web request/response adapters, Pagaí review endpoints, and authenticated company-selection endpoint.
- `src/Hosts/Vinx.Erp.WorkerHost/`: common inbound Agent handler plus existing channel provider handlers.
- `apps/web/src/features/{agent,paguai}/`: existing Agent request surface and immutable Pagaí proposal review.
- `scripts/` and `tests/tools/`: deterministic/real launcher switch and sanitized process composition.
- `docs/verification/335-real-agent-multichannel-test-cases.md`: the only new verification document.

## Estimate and Single-PR Exception

- 70–80 counted non-test files from the concrete map below;
- 8,000–11,000 manual changed lines, including tests and hand-written migration SQL;
- 7–10 generated designer/snapshot/lock artifacts reported separately;
- three focused database migrations: execution ledger, Pagaí proposals, and channel adapter state.

This exceeds the repository’s normal decomposition thresholds. The owner
explicitly requires one PR. Do not expand beyond the listed behavior: reuse an
existing file when ownership stays clear, and stop for scope correction if the
branch would exceed 80 counted files or 11,000 manual lines.

---

### Task 1: Provider-Agnostic Credential and Hardened Transport

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/AgentProviderCredential.cs`
- Create: `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleTransport.cs`
- Modify: `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleOptions.cs`
- Modify: `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleChatClient.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentRunOptions.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentRunServiceCollectionExtensions.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Providers/OpenAiCompatible/AgentProviderCredentialTests.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Providers/OpenAiCompatible/OpenAiCompatibleTransportTests.cs`
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/AgentRunCompositionTests.cs`

**Interfaces:**
- Produces: `AgentProviderCredential.Resolve(Func<string, string?> readEnvironment)`, `OpenAiCompatibleTransport.Create(OpenAiCompatibleOptions profile, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)`, and the internal testable `ResolveApprovedAddressesAsync` used by `ConnectCallback`.
- Consumes: existing `ProviderEgressPolicy.ValidateEndpoint` and `ValidateResolvedAddresses`.

- [ ] **Step 1: Write the failing credential and transport tests**

```csharp
[Fact]
public void Resolver_accepts_only_the_dedicated_process_variable()
{
    var credential = AgentProviderCredential.Resolve(
        name => name == "VINX_AGENT_PROVIDER_API_KEY" ? "synthetic-test-key" : "ignored");
    Assert.NotNull(credential);
}

[Fact]
public async Task Transport_rejects_a_private_DNS_result_before_connecting()
{
    await Assert.ThrowsAsync<ProviderEgressRejectedException>(() =>
        OpenAiCompatibleTransport.ResolveApprovedAddressesAsync(
            new Uri("https://api.example.test"),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.2") }),
            CancellationToken.None));
}
```

Add behavioral cases for absent/blank/control/multiline keys; attempts to supply `AgentRun__ApiKey`; HTTPS/443; exact allowlist; redirects, proxy and cookies disabled; public DNS accepted; any prohibited A/AAAA rejected; cancellation propagated.

- [ ] **Step 2: Run RED**

Run:

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentProviderCredentialTests|FullyQualifiedName~OpenAiCompatibleTransportTests'
```

Expected: compilation/test failures because the credential and transport types do not exist.

- [ ] **Step 3: Implement the minimum BCL/SDK composition**

```csharp
internal sealed class AgentProviderCredential
{
    internal const string EnvironmentVariable = "VINX_AGENT_PROVIDER_API_KEY";
    internal static AgentProviderCredential Resolve(Func<string, string?> readEnvironment);
    internal ApiKeyCredential ToApiKeyCredential();
}
```

Remove bindable `ApiKey` properties from host options. Keep endpoint, model, modalities, budgets and `ApprovedHosts` server-side. Build `SocketsHttpHandler` with `AllowAutoRedirect=false`, `UseProxy=false`, `UseCookies=false`, and connection-time DNS validation; keep SDK retry count zero.

- [ ] **Step 4: Run GREEN and composition consumers**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentProviderCredentialTests|FullyQualifiedName~OpenAiCompatibleTransportTests|FullyQualifiedName~OpenAiCompatibleChatClientContractTests'
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentRunCompositionTests'
```

- [ ] **Step 5: Secret scan and commit**

Stage only the listed files, run the redacted staged-file scanner, then commit:

```bash
git commit -m "feat(ai): harden compatible provider runtime"
```

---

### Task 2: Durable Agent Execution Ledger

**Files:**
- Create: `src/Foundation/Vinx.Erp.Foundation/Domain/AgentExecutionLedger.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/AgentExecutionLedgerConfiguration.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Persistence/FoundationDbContext.cs`
- Generate: Foundation migration named `AddAgentExecutionLedger` and its designer/snapshot update
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/AgentExecutionLedgerTests.cs`
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/AgentExecutionLedgerMigrationTests.cs`

**Interfaces:**
- Produces: monotonic `Claimed -> VisionInFlight -> ModelInFlight -> Completed|FailedClosed|Ambiguous` entity methods and unique key `(CompanyId, ActorUserId, Channel, RequestId)`.
- Consumes: existing Foundation company/RLS/audit conventions; does not modify generic `IdempotencyRecord`.

- [ ] **Step 1: Write lifecycle and schema RED tests**

```csharp
[Fact]
public void Model_in_flight_cannot_move_back_to_vision()
{
    var actorId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    var requestId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    var ledger = AgentExecutionLedger.Create(
        Guid.Parse("33333333-3333-4333-8333-333333333333"),
        Guid.Parse("44444444-4444-4444-8444-444444444444"),
        actorId,
        "web",
        null,
        requestId,
        "read_only_summarize_v1",
        new string('a', 64));

    ledger.MarkModelInFlight(actorId, requestId);

    Assert.Throws<InvalidOperationException>(() =>
        ledger.MarkVisionInFlight(actorId, requestId));
}
```

Cover exact state allowlist, payload hash format, safe result only on terminal state, unique command key, company FK, RLS forced, runtime least privilege, and absence of prompt/content/address columns.

- [ ] **Step 2: Run unit RED**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentExecutionLedgerTests'
```

- [ ] **Step 3: Implement entity/configuration and generate one migration**

```csharp
public sealed class AgentExecutionLedger : ICompanyScoped
{
    public static AgentExecutionLedger Create(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        string channel,
        Guid? inboundMessageId,
        Guid requestId,
        string journey,
        string payloadHash);
    public void MarkVisionInFlight(Guid actorUserId, Guid requestId);
    public void MarkModelInFlight(Guid actorUserId, Guid requestId);
    public void Complete(string safeResultJson, Guid actorUserId, Guid requestId);
    public void FailClosed(string reasonCode, Guid actorUserId, Guid requestId);
    public void MarkAmbiguous(string reasonCode, Guid actorUserId, Guid requestId);
}
```

Generate with the repository’s locked EF tooling and add hand-written RLS/grants/checks to the migration. Hand-written SQL counts as manual lines.

- [ ] **Step 4: Run the one focused database class**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~AgentExecutionLedgerMigrationTests'
```

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(ai): add durable execution ledger"
```

---

### Task 3: Shared Execution Service and Web Adapter

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/AgentCommand.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentExecutionResult.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentExecutionLifecycle.cs`
- Create: `src/Modules/Vinx.Erp.AI/AgentExecutionService.cs`
- Modify: `src/Modules/Vinx.Erp.AI/AgentRunner.cs`
- Modify: `src/Modules/Vinx.Erp.AI/AgentOutcome.cs`
- Modify: `src/Modules/Vinx.Erp.AI/DocumentContext/DocumentContextNormalizer.cs`
- Modify: `src/Modules/Vinx.Erp.AI/DocumentContext/DocumentVisionOcr.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentExecutionApplicationService.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentEndpoints.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentHttpContracts.cs`
- Test: existing `AgentRunner*`, `AgentRunCompositionTests`, `AgentEndpointTests`

**Interfaces:**
- Produces: `AgentExecutionService.ExecuteAsync(AgentCommand, CancellationToken)` and safe dispositions `Completed`, `InProgress`, `Ambiguous`, `Conflict`, `FailedClosed`.
- Consumes: Task 1 provider runtime and Task 2 ledger.

- [ ] **Step 1: Write RED tests for shared parity and pre-dispatch claims**

Add `Concurrent_same_request_claims_once_before_the_model`,
`Completed_same_hash_replays_the_safe_result`,
`Changed_payload_returns_idempotency_conflict`,
`Vision_state_changes_only_at_the_external_OCR_boundary`, and
`Ambiguous_execution_never_calls_the_model_again`. Each test asserts the real
service result plus the fake model’s single observable invocation count.

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentRunner|FullyQualifiedName~AgentExecution'
```

- [ ] **Step 3: Implement the concrete shared service**

```csharp
public sealed class AgentExecutionService
{
    public Task<AgentExecutionResult> ExecuteAsync(
        AgentCommand command,
        CancellationToken cancellationToken);
}

internal interface IAgentExecutionLifecycle
{
    Task MarkVisionInFlightAsync(CancellationToken cancellationToken);
    Task MarkModelInFlightAsync(CancellationToken cancellationToken);
}
```

Keep HTTP status mapping in ApiHost. Remove the old post-provider `agent.run` `IdempotencyRecord` path only after ledger replay/conflict tests are green.

- [ ] **Step 4: Run GREEN with the focused Web integration class**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~AgentRunner|FullyQualifiedName~AgentExecution'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~AgentEndpointTests'
```

- [ ] **Step 5: Commit**

```bash
git commit -m "refactor(ai): share guarded agent execution"
```

---

### Task 4: Strict Pagaí Document Journey

**Files:**
- Create: `src/Modules/Vinx.Erp.AI/Guardrails/PaguaiDocumentProposalContracts.cs`
- Create: `src/Modules/Vinx.Erp.AI/Guardrails/PaguaiDocumentProposalParser.cs`
- Modify: `src/Modules/Vinx.Erp.AI/Guardrails/AgentOutputGate.cs`
- Modify: `src/Modules/Vinx.Erp.AI/Guardrails/AgentOutputContract.cs`
- Modify: `src/Modules/Vinx.Erp.AI/AgentTaskProfileCatalog.cs`
- Modify: `src/Modules/Vinx.Erp.AI/AgentRunner.cs`
- Modify only if required: `src/Modules/Vinx.Erp.AI/DocumentContext/OcrContextContracts.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/PaguaiDocumentProposalParserTests.cs`
- Test: `tests/public/Vinx.Erp.AI.UnitTests/AgentRunnerGateIntegrationTests.cs`

**Interfaces:**
- Produces: pure `PaguaiDocumentProposalParser.Parse(PaguaiDocumentProposalParseRequest)` and profile `paguai_from_document_v1` requiring exactly one attachment.
- Consumes: authorized ephemeral source/page set from DocumentContext and `PaguaiAmountRules`.

- [ ] **Step 1: Write table-driven RED cases with hand-derived JSON**

Create `PaguaiDocumentProposalParserTests.InvalidClosedEnvelopes` with literal
JSON/expected-reason rows. Cases: unknown/missing/type fields, numeric amount,
scale >2, non-BRL, bad date, bad proof requirement, wrong/foreign file ID,
absent required source field, unauthorized page, confidence <0.80,
non-financial and insufficient evidence. Assert parser status/reason/typed
proposal directly; do not assert mocks.

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~PaguaiDocumentProposal'
```

- [ ] **Step 3: Implement the closed parser and profile**

```csharp
public sealed record PaguaiDocumentProposalParseRequest(
    Guid FileRecordId,
    IReadOnlySet<string> AuthorizedSourceReferences,
    string StructuredJson);

public sealed record PaguaiDocumentProposalParseResult(
    PaguaiProposalParseStatus Status,
    string? Reason,
    PaguaiDocumentProposal? Proposal);
```

Return typed safe fields only. Never copy OCR text or raw JSON into the proposal result.

- [ ] **Step 4: Run GREEN and all AI unit tests once**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
```

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(ai): add typed paguai document journey"
```

---

### Task 5: Pagaí Proposal Lifecycle and Atomic Confirmation API

**Files:**
- Create: `src/Foundation/Vinx.Erp.Foundation/Domain/PaguaiDraftProposal.cs`
- Create: `src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/PaguaiDraftProposalConfiguration.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Domain/PaguaiRequest.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Domain/FileRecord.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/PaguaiRequestConfiguration.cs`
- Modify: `src/Foundation/Vinx.Erp.Foundation/Persistence/FoundationDbContext.cs`
- Generate: Foundation migration named `AddPaguaiDraftProposals` and its designer/snapshot update
- Create: `src/Modules/Vinx.Erp.Paguai/PaguaiProposalContracts.cs`
- Create: `src/Modules/Vinx.Erp.Paguai/PaguaiProposalService.cs`
- Create: `src/Modules/Vinx.Erp.Paguai/PaguaiProposalConfirmationService.cs`
- Modify: `src/Modules/Vinx.Erp.Paguai/PaguaiDraftService.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Controllers/PaguaiController.cs`
- Create: `src/Hosts/Vinx.Erp.ApiHost/Models/ConfirmPaguaiProposalRequest.cs`
- Create: `src/Hosts/Vinx.Erp.ApiHost/Models/RejectPaguaiProposalRequest.cs`
- Test: `tests/public/Vinx.Erp.Foundation.UnitTests/PaguaiDraftProposalLifecycleTests.cs`
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/PaguaiProposalEndpointTests.cs`
- Test: `tests/public/Vinx.Erp.Foundation.IntegrationTests/PaguaiProposalSchemaTests.cs`

**Interfaces:**
- Produces: proposal GET/confirm/reject endpoints and `PaguaiProposalConfirmationService` owning one transaction.
- Consumes: Task 3 safe execution result and Task 4 typed proposal.

- [ ] **Step 1: Write lifecycle/atomicity RED tests**

Add literal scenarios for safe-field persistence, actor/company RLS,
15-minute DB-clock expiry, same-request replay, payload conflict,
rejected/invalidated/consumed unavailable, revoked membership/capability/file,
rollback of classification/draft/proposal/audit, antiforgery, and
`Concurrent_confirmations_create_one_draft_and_consume_once`. The concurrent
test reads the database and asserts exactly one `source_proposal_id` row.

- [ ] **Step 2: Run unit RED, then the one stable Pagaí integration slice**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~PaguaiDraftProposal|FullyQualifiedName~FileRecordLifecycle'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~PaguaiProposal'
```

- [ ] **Step 3: Implement the domain and single transaction**

```csharp
public sealed class PaguaiProposalConfirmationService
{
    public Task<PaguaiProposalConfirmationResult> ConfirmAsync(
        ActorContext actor,
        ConfirmPaguaiProposalCommand command,
        CancellationToken cancellationToken);

    public Task<PaguaiProposalMutationResult> RejectAsync(
        ActorContext actor,
        RejectPaguaiProposalCommand command,
        CancellationToken cancellationToken);
}
```

Extract only pure draft validation/construction from `PaguaiDraftService`; never call its transaction-opening `CreateAsync` from confirmation. Promote only confirmed `agentic_attachment` to `paguai_request_invoice` inside the same transaction. Add unique `source_proposal_id`.

- [ ] **Step 4: Generate migration, run GREEN, and verify schema snapshot**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~PaguaiDraftProposal|FullyQualifiedName~FileRecordLifecycle'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~PaguaiProposal'
```

The public database verifier remains part of the single final gate; do not run
it again here.

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(paguai): confirm agent proposals atomically"
```

---

### Task 6: Immutable Web Proposal Review

**Files:**
- Modify: `apps/web/src/features/paguai/paguai-draft-client.ts`
- Modify: `apps/web/src/features/paguai/paguai-draft-client.test.ts`
- Create: `apps/web/src/features/paguai/paguai-proposal-review-dialog.tsx`
- Create: `apps/web/src/features/paguai/paguai-proposal-review-dialog.test.tsx`
- Modify: `apps/web/src/features/paguai/paguai-page.tsx`
- Modify: `apps/web/src/features/paguai/paguai-page.test.tsx`
- Modify: `apps/web/src/shared/routing/app-paths.ts`
- Modify: `apps/web/src/shared/routing/app-paths.test.ts`

**Interfaces:**
- Produces: allow-listed `/paguai?proposal={proposalId}` route state and exact persisted proposal confirm/reject UI.
- Consumes: Task 5 authenticated API; reuses existing antiforgery and feedback primitives.

- [ ] **Step 1: Write RED component/client behavior**

Add `confirms exactly the persisted proposal without editable financial
inputs`, using the existing MSW/API fixture rather than a mocked component.
Also cover invalid proposal UUID, company switch, expired/rejected/consumed
state, confirm/reject antiforgery, refresh after success, and no raw source/OCR
rendering.

- [ ] **Step 2: Run RED**

```bash
npm --workspace apps/web run test -- --run src/features/paguai/paguai-draft-client.test.ts src/features/paguai/paguai-proposal-review-dialog.test.tsx src/features/paguai/paguai-page.test.tsx
```

- [ ] **Step 3: Implement the smallest dialog on the existing Pagaí page**

```typescript
export async function getPaguaiProposal(companyId: string, proposalId: string): Promise<PaguaiProposal>;
export async function confirmPaguaiProposal(proposalId: string, requestId: string): Promise<PaguaiDraft>;
export async function rejectPaguaiProposal(proposalId: string, requestId: string): Promise<void>;
```

Do not add another page shell or state framework. Parse the query parameter
through `app-paths`, fetch the proposal under the selected company, show
immutable typed fields, and expose only confirm/reject.

- [ ] **Step 4: Run GREEN, lint and format only Web**

```bash
npm --workspace apps/web run test -- --run src/features/paguai/paguai-draft-client.test.ts src/features/paguai/paguai-proposal-review-dialog.test.tsx src/features/paguai/paguai-page.test.tsx
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
```

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(web): review agent paguai proposals"
```

---

### Task 7: WhatsApp Adapter Through the Shared Core

**Files:**
- Create: `src/Modules/Vinx.Erp.Notifications/InboundAgentRequestReceived.cs`
- Create: `src/Modules/Vinx.Erp.Notifications/InboundAgentChannelAdapter.cs`
- Create: `src/Modules/Vinx.Erp.Notifications/InboundAttachmentMaterializer.cs`
- Create: `src/Hosts/Vinx.Erp.WorkerHost/InboundAgentRequestReceivedHandler.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Notifications/InboundWhatsAppIngressService.cs`
- Modify: `src/Modules/Vinx.Erp.Notifications/Providers/Meta/InboundWhatsAppInteractionService.cs`
- Modify: `src/Modules/Vinx.Erp.Notifications/Providers/Meta/InboundWhatsAppDocumentService.cs`
- Modify: `src/Modules/Vinx.Erp.Notifications/Providers/Meta/InboundWhatsAppAiOptions.cs`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/InboundWhatsAppImageReceivedHandler.cs`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/InboundWhatsAppDocumentReceivedHandler.cs`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/InboundWhatsAppTextCorrelationExpiredHandler.cs`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/Program.cs`
- Generate: Foundation migration named `AddWhatsAppAgentAdapter` for narrow claim/read/finalize/outbox state
- Test: existing `MetaWhatsAppInbound*`, `InboundWhatsAppReplyStateFunctionTests`, plus `InboundWhatsAppAgentAdapterTests.cs`

**Interfaces:**
- Produces: opaque `InboundAgentRequestReceived(Guid InboundMessageId, AgentChannelKind Channel)` and one concrete channel adapter; no provider access from Meta classes.
- Consumes: Tasks 1–5 shared service/materialization/proposal result.

- [ ] **Step 1: Write RED tests for admission-to-core and reply barriers**

Add `Admitted_document_reaches_the_shared_core_with_only_a_confirmed_file_id`.
Cover invalid `X-Hub-Signature-256`, channel, unbound/ambiguous identity and
revoked capability = zero core calls; text/image/document use approved journeys; direct
`OpenAiCompatibleChatClient` construction removed; reply replay/timeout/
transport ambiguity preserves `reply_in_flight` and `reply_ambiguous`; Pagaí sends only the
server-generated review link.

- [ ] **Step 2: Run the focused WhatsApp slice once for RED**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~MetaWhatsApp|FullyQualifiedName~InboundWhatsAppReply|FullyQualifiedName~InboundWhatsAppAgentAdapter'
```

- [ ] **Step 3: Extract materialization and replace direct AI authority**

```csharp
public sealed record InboundAgentRequestReceived(
    Guid InboundMessageId,
    AgentChannelKind Channel);
```

`InboundAttachmentMaterializer` accepts already-downloaded trusted bytes plus admitted internal identity, applies existing scan/storage/FileRecord lifecycle, and returns only the confirmed ID. It has exactly two consumers after Task 8; do not make it a generic storage framework.

- [ ] **Step 4: Run GREEN and WorkerHost consumer tests**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~MetaWhatsApp|FullyQualifiedName~InboundWhatsAppReply|FullyQualifiedName~InboundWhatsAppAgentAdapter'
dotnet test tests/public/Vinx.Erp.Hosts.IntegrationTests/Vinx.Erp.Hosts.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~InboundWhatsApp|FullyQualifiedName~Worker'
```

- [ ] **Step 5: Commit**

```bash
git commit -m "refactor(whatsapp): route inbound through agent core"
```

---

### Task 8: Telegram Adapter, Attachments, and Authenticated Company Selection

**Files:**
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Notifications/TelegramWebhookEndpoints.cs`
- Modify: `src/Hosts/Vinx.Erp.ApiHost/Notifications/InboundTelegramIngressService.cs`
- Create: `src/Modules/Vinx.Erp.Notifications/Providers/Telegram/TelegramMediaClient.cs`
- Modify: `src/Modules/Vinx.Erp.Notifications/Providers/Telegram/TelegramClient.cs`
- Modify: `src/Modules/Vinx.Erp.Notifications/Providers/Telegram/TelegramMapper.cs`
- Modify: `src/Hosts/Vinx.Erp.WorkerHost/InboundTelegramTextReceivedHandler.cs`
- Modify: `src/Modules/Vinx.Erp.Notifications/InboundAgentChannelAdapter.cs`
- Create: `src/Hosts/Vinx.Erp.ApiHost/Notifications/AgentChannelCompanySelectionEndpoints.cs`
- Modify: `apps/web/src/features/agent/agent-page.tsx`
- Modify: `apps/web/src/features/agent/agent.test.tsx`
- Generate: Foundation migration named `AddTelegramAgentAdapter` for message type/media metadata, `selection_required`, preference, and separate agent claim/read/finalize state
- Test: existing `TelegramInbound*`, `TelegramWebhookTests`, `TelegramOutbound*`, plus `TelegramAgentAdapterTests.cs`

**Interfaces:**
- Produces: Telegram text/photo/document mapping to the same opaque Agent envelope and an authenticated selection endpoint for exactly one resolved actor with multiple memberships.
- Consumes: Task 7 materializer/common adapter and existing Telegram provider/outbox.

- [ ] **Step 1: Write RED tests without contacting Telegram**

Add `Selection_required_never_calls_the_model_and_accepts_only_the_logged_in_actor`.
Cover secret/webhook failures, unbound/unverified/inactive/multi-user
fail-closed, unique-user/multi-company selection, revoked preference,
text/photo/document mapping, provider file-path allowlist, attachment limits,
technical receipt unchanged, agent claim states separate, outbox replay and
ambiguous external send.

- [ ] **Step 2: Run focused Telegram RED**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~Telegram'
```

- [ ] **Step 3: Implement only the merged-contract extensions**

```csharp
public sealed record SelectAgentChannelCompanyRequest(
    Guid InboundMessageId,
    Guid CompanyId,
    Guid RequestId);
```

Keep `completed` as technical receipt. Add separate agent state/functions. Persist `selection_required` only when the resolver proves one internal user and multiple active memberships; all other ambiguity receives no reply. A new message after authenticated selection creates the run. Model output never supplies review URLs.

- [ ] **Step 4: Run GREEN and the Telegram Host consumer slice**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~Telegram'
dotnet test tests/public/Vinx.Erp.Hosts.IntegrationTests/Vinx.Erp.Hosts.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~Telegram'
npm --workspace apps/web run test -- --run src/features/agent/agent.test.tsx
```

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(telegram): add shared agent adapter"
```

---

### Task 9: Deterministic/Real Fixture and Authorized Smoke Wiring

**Files:**
- Modify: `scripts/run-real-e2e.mjs`
- Modify: `scripts/run-real-e2e.test.mjs`
- Modify: `tests/tools/Vinx.Erp.Foundation.E2eHost/Program.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureHost.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/SharedE2eFixtureHost.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureData.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/DeterministicE2eAgentModel.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/E2eFixtureHostOptionsTests.cs`
- Modify: `tests/public/Vinx.Erp.Foundation.IntegrationTests/RealE2eFixtureHostTests.cs`
- Modify: `docs/runbooks/real-e2e-fixture.md`

**Interfaces:**
- Produces: exact `--agent-mode deterministic|real`; deterministic has no external network, real receives the key only in server processes and exposes sanitized readiness.
- Consumes: complete provider/core/Pagaí/channel behavior.

```bash
npm run dev:real-e2e -- --profiles maria,clara --agent-mode deterministic
npm run dev:real-e2e -- --profiles maria,clara --agent-mode real
```

- [ ] **Step 1: Build the documented host prerequisite and write launcher RED tests**

```bash
dotnet build tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --no-restore
node --test scripts/run-real-e2e.test.mjs
```

New cases must fail before implementation: unknown/missing mode; real mode without key fails before host/Docker start; deterministic mode strips any provider key; real mode never passes key to Vite/readiness; no endpoint/model comes from browser; cleanup owns only its processes/resources.

- [ ] **Step 2: Implement mode parsing and server-only propagation**

```javascript
const AGENT_MODES = new Set(["deterministic", "real"]);
```

In deterministic mode register only `DeterministicE2eAgentModel`. In real mode configure the OpenAI-compatible profile from fixed test-only endpoint/model metadata and resolve the process key through Task 1.

- [ ] **Step 3: Add the synthetic dataset and deterministic proposal proof**

Seed Maria/Alfa with the five required capabilities and one synthetic `agentic_attachment`; Maria/Beta and Clara/Gama remain denied. Verify shared PostgreSQL/RLS, proposal review and one draft after confirmation. Do not seed channel tokens/addresses or raw OCR/provider output.

- [ ] **Step 4: Run the stable fixture slice once**

Before Docker, attribute every VINX process/worktree/container per `docs/runbooks/public-verification.md`. Then run:

```bash
node --test scripts/run-real-e2e.test.mjs
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~E2eFixtureHostOptionsTests|FullyQualifiedName~E2eFixtureSeederTests|FullyQualifiedName~RealE2eFixtureHostTests'
```

Do not run the real provider smoke yet.

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(e2e): add real agent demo mode"
```

---

### Task 10: Verification Manifest, Final Review, One Fix Wave, and Delivery

**Files:**
- Create: `docs/verification/335-real-agent-multichannel-test-cases.md`
- Modify only durable contract changes: `docs/modules/ai.md`, `docs/modules/notifications.md`, `docs/modules/paguai.md`, `docs/runbooks/telegram-inbound.md`, `docs/runbooks/whatsapp-inbound.md`

**Interfaces:**
- Produces: one short BDD-to-test map and updated existing contracts; no new report or handoff document.
- Consumes: evidence from Tasks 1–9.

- [ ] **Step 1: Recalculate actual scope and freeze**

Count all non-test changed files; count all manual added/deleted lines including tests and hand-written migration SQL; list only designer/snapshot/lock output as generated. If the implementation exceeds the approved estimate, record the concrete reason in issue #335 and the PR before continuing.

- [ ] **Step 2: Run affected projects once**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.AI.IntegrationTests/Vinx.Erp.AI.IntegrationTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~Agent|FullyQualifiedName~PaguaiProposal|FullyQualifiedName~WhatsApp|FullyQualifiedName~Telegram|FullyQualifiedName~E2eFixture'
dotnet test tests/public/Vinx.Erp.Hosts.IntegrationTests/Vinx.Erp.Hosts.IntegrationTests.csproj --no-restore
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm run test:no-direct-supabase
node --test scripts/run-real-e2e.test.mjs
```

- [ ] **Step 3: Dispatch one independent Sol whole-branch review**

Review requirements, security, tenant isolation, idempotency, audit, channel ambiguity, cleanup, secret handling, and test strength. Consolidate all Critical/Important findings into one fix task. Run one scoped re-review after that fix; do not enter micro-fix loops.

- [ ] **Step 4: Run the single final public gate**

After resource attribution and scope freeze:

```bash
./scripts/verify-public.sh
```

- [ ] **Step 5: Run the authorized real smoke exactly once per role**

Use synthetic text and a synthetic image only. Start real mode with `VINX_AGENT_PROVIDER_API_KEY` already present in the server process environment. Make no channel send. Record only model ID, safe status category and approximate latency; never record prompt, response, key, endpoint headers or image.

- [ ] **Step 6: Secret scan, commit documentation, push and create the single PR**

```bash
git commit -m "docs: record real agent verification"
git push -u origin codex/real-agent-multichannel
```

Open one PR initially against `codex/professional-multicompany-ui`; after PR #332 merges, update from `origin/main` and retarget the same PR. Link issue #335 and include focused tests, final gate, smoke limits, actual size, generated-file count, independent review, and explicit evidence boundaries.
