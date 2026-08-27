using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class AgentExecutionLedgerTests
{
    [Fact]
    public void Model_in_flight_cannot_move_back_to_vision()
    {
        var actorId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var requestId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var ledger = CreateLedger(actorId, requestId);

        ledger.MarkVisionInFlight(actorId, requestId);
        ledger.MarkModelInFlight(actorId, requestId);

        Assert.Throws<InvalidOperationException>(() =>
            ledger.MarkVisionInFlight(actorId, requestId));
    }

    [Fact]
    public void Claimed_execution_can_move_directly_to_model_for_text_only_work()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Claimed, ledger.State);
        ledger.MarkModelInFlight(actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.ModelInFlight, ledger.State);
    }

    [Fact]
    public void Lifecycle_allows_vision_then_model_but_locks_terminal_states()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        ledger.MarkVisionInFlight(actorId, requestId);
        ledger.MarkModelInFlight(actorId, requestId);
        ledger.Complete("{}", actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Completed, ledger.State);
        Assert.Throws<InvalidOperationException>(() => ledger.MarkModelInFlight(actorId, requestId));
        Assert.Throws<InvalidOperationException>(() => ledger.MarkAmbiguous("provider_timeout", actorId, requestId));
    }

    [Fact]
    public void Fail_closed_is_allowed_only_before_provider_dispatch()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var claimed = CreateLedger(actorId, requestId);
        var modelInFlight = CreateLedger(actorId, requestId);

        claimed.FailClosed("policy_denied", actorId, requestId);
        modelInFlight.MarkModelInFlight(actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.FailedClosed, claimed.State);
        Assert.Throws<InvalidOperationException>(() => modelInFlight.FailClosed("provider_unavailable", actorId, requestId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ambiguous_is_allowed_after_vision_or_model_dispatch(bool throughVision)
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        if (throughVision)
        {
            ledger.MarkVisionInFlight(actorId, requestId);
        }
        else
        {
            ledger.MarkModelInFlight(actorId, requestId);
        }

        ledger.MarkAmbiguous("provider_timeout", actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Ambiguous, ledger.State);
        Assert.Equal("provider_timeout", ledger.ReasonCode);
    }

    [Fact]
    public void Deterministic_pre_dispatch_result_can_complete_from_claimed()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        ledger.Complete("{\"status\":\"needs_clarification\"}", actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Completed, ledger.State);
        Assert.Equal("{\"status\":\"needs_clarification\"}", ledger.SafeResultJson);
        Assert.Null(ledger.ReasonCode);
    }

    [Fact]
    public void Complete_remains_disallowed_from_vision_without_main_dispatch()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        Assert.Throws<InvalidOperationException>(() => ledger.MarkAmbiguous("provider_timeout", actorId, requestId));
        ledger.MarkVisionInFlight(actorId, requestId);
        Assert.Throws<InvalidOperationException>(() => ledger.Complete("{}", actorId, requestId));
        ledger.MarkModelInFlight(actorId, requestId);
        ledger.Complete("{}", actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Completed, ledger.State);
    }

    [Fact]
    public void Terminal_result_is_available_only_after_successful_completion()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        Assert.Null(ledger.SafeResultJson);

        ledger.MarkVisionInFlight(actorId, requestId);
        ledger.MarkModelInFlight(actorId, requestId);
        ledger.Complete("""{"outcome":"completed"}""", actorId, requestId);

        Assert.Equal(AgentExecutionLedgerState.Completed, ledger.State);
        Assert.Equal("""{"outcome":"completed"}""", ledger.SafeResultJson);
        Assert.Null(ledger.ReasonCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")]
    public void Create_rejects_payload_hashes_that_are_not_lowercase_sha256(string? payloadHash)
    {
        Assert.Throws<ArgumentException>(() => AgentExecutionLedger.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "web",
            null,
            Guid.NewGuid(),
            "read_only_summarize_v1",
            payloadHash!));
    }

    [Fact]
    public void Lifecycle_rejects_a_different_actor_or_request()
    {
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var ledger = CreateLedger(actorId, requestId);

        Assert.Throws<InvalidOperationException>(() => ledger.MarkVisionInFlight(Guid.NewGuid(), requestId));
        Assert.Throws<InvalidOperationException>(() => ledger.MarkVisionInFlight(actorId, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("web", true)]
    [InlineData("whatsapp", false)]
    [InlineData("telegram", false)]
    [InlineData("email", true)]
    public void Create_rejects_channels_without_the_required_inbound_message_shape(
        string channel,
        bool inboundMessageIsPresent)
    {
        Assert.Throws<ArgumentException>(() => AgentExecutionLedger.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            channel,
            inboundMessageIsPresent ? Guid.NewGuid() : null,
            Guid.NewGuid(),
            "read_only_summarize_v1",
            new string('a', 64)));
    }

    [Theory]
    [InlineData("web", false)]
    [InlineData("whatsapp", true)]
    [InlineData("telegram", true)]
    public void Create_accepts_each_supported_channel_with_its_required_inbound_message_shape(
        string channel,
        bool inboundMessageIsPresent)
    {
        var ledger = AgentExecutionLedger.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            channel,
            inboundMessageIsPresent ? Guid.NewGuid() : null,
            Guid.NewGuid(),
            "read_only_summarize_v1",
            new string('a', 64));

        Assert.Equal(channel, ledger.Channel);
    }

    [Fact]
    public void Ledger_starts_with_a_concurrency_row_version()
    {
        var ledger = CreateLedger(Guid.NewGuid(), Guid.NewGuid());

        Assert.IsAssignableFrom<IConcurrencyVersioned>(ledger);
        Assert.Equal(1L, ledger.RowVersion);
    }

    private static AgentExecutionLedger CreateLedger(Guid actorId, Guid requestId) => AgentExecutionLedger.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        actorId,
        "web",
        null,
        requestId,
        "read_only_summarize_v1",
        new string('a', 64));
}
