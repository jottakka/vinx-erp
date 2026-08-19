using Microsoft.AspNetCore.Http;
using Vinx.Erp.AI;
using Vinx.Erp.ApiHost.Agent;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class AgentOutcomeMappingTests
{
    private static readonly Guid CorrelationId = Guid.Parse("8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    private static AgentOutcome Outcome(
        AgentOutcomeStatus status,
        string reasonCode,
        string? text = null,
        string? proposedActionName = null) =>
        new(status, reasonCode, text, proposedActionName, CorrelationId, TimeSpan.FromMilliseconds(5));

    [Fact]
    public void Completed_maps_to_200_result_preserving_redacted_text()
    {
        var result = AgentExecutionApplicationService.MapOutcome(Outcome(
            AgentOutcomeStatus.Completed,
            AgentOutcomeReason.Completed,
            "Resumo aprovado com [email-redigido] preservado."));

        Assert.NotNull(result.Response);
        Assert.Null(result.ErrorCode);
        Assert.Equal(StatusCodes.Status200OK, result.HttpStatusCode);
        Assert.Equal(AgentRunStatus.Result, result.Response.Status);
        Assert.Equal(AgentOutcomeReason.Completed, result.Response.ReasonCode);
        Assert.Equal("Resumo aprovado com [email-redigido] preservado.", result.Response.Text);
        Assert.Equal(CorrelationId, result.Response.CorrelationId);
    }

    [Fact]
    public void NeedsClarification_maps_to_200_typed_status()
    {
        var result = AgentExecutionApplicationService.MapOutcome(Outcome(
            AgentOutcomeStatus.NeedsClarification,
            AgentOutcomeReason.CompanyRequired,
            "Informe a empresa para continuar."));

        Assert.NotNull(result.Response);
        Assert.Equal(StatusCodes.Status200OK, result.HttpStatusCode);
        Assert.Equal(AgentRunStatus.NeedsClarification, result.Response.Status);
        Assert.Equal(AgentOutcomeReason.CompanyRequired, result.Response.ReasonCode);
        Assert.Equal("Informe a empresa para continuar.", result.Response.Text);
    }

    [Fact]
    public void Denied_maps_to_200_typed_status()
    {
        var result = AgentExecutionApplicationService.MapOutcome(Outcome(
            AgentOutcomeStatus.Denied,
            AgentOutcomeReason.CapabilityDenied,
            "Acesso negado."));

        Assert.NotNull(result.Response);
        Assert.Equal(StatusCodes.Status200OK, result.HttpStatusCode);
        Assert.Equal(AgentRunStatus.Denied, result.Response.Status);
        Assert.Equal(AgentOutcomeReason.CapabilityDenied, result.Response.ReasonCode);
        Assert.Equal("Acesso negado.", result.Response.Text);
    }

    [Fact]
    public void RequiresConfirmation_maps_to_200_without_text_or_action()
    {
        var result = AgentExecutionApplicationService.MapOutcome(Outcome(
            AgentOutcomeStatus.RequiresConfirmation,
            AgentOutcomeReason.ToolProposalRequiresConfirmation,
            "Texto redigido que NÃO viaja no v1.",
            proposedActionName: "summarize_document"));

        Assert.NotNull(result.Response);
        Assert.Equal(StatusCodes.Status200OK, result.HttpStatusCode);
        Assert.Equal(AgentRunStatus.RequiresConfirmation, result.Response.Status);
        Assert.Equal(AgentOutcomeReason.ToolProposalRequiresConfirmation, result.Response.ReasonCode);
        // A ação proposta permanece opaca: nem o texto nem o nome viajam na resposta.
        Assert.Null(result.Response.Text);
    }

    [Fact]
    public void Invalid_maps_to_400_validation_error()
    {
        var result = AgentExecutionApplicationService.MapOutcome(Outcome(
            AgentOutcomeStatus.Invalid,
            AgentOutcomeReason.MessageEmpty));

        Assert.NotNull(result.Response);
        Assert.Equal(StatusCodes.Status400BadRequest, result.HttpStatusCode);
        Assert.Equal(AgentRunStatus.ValidationError, result.Response.Status);
        Assert.Equal(AgentOutcomeReason.MessageEmpty, result.Response.ReasonCode);
        Assert.Null(result.Response.Text);
        Assert.Equal(CorrelationId, result.Response.CorrelationId);
    }

    [Theory]
    [InlineData(AgentOutcomeReason.ProviderNotConfigured)]
    [InlineData(AgentOutcomeReason.ModelUnavailable)]
    [InlineData(AgentOutcomeReason.ContextUnavailable)]
    public void Unavailable_maps_to_503_error_envelope_with_outcome_reason(string reasonCode)
    {
        var result = AgentExecutionApplicationService.MapOutcome(Outcome(
            AgentOutcomeStatus.Unavailable,
            reasonCode));

        Assert.Null(result.Response);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.HttpStatusCode);
        Assert.Equal(reasonCode, result.ErrorCode);
    }

    [Fact]
    public void TimedOut_maps_to_504_agent_timeout_code()
    {
        var result = AgentExecutionApplicationService.MapOutcome(Outcome(
            AgentOutcomeStatus.TimedOut,
            AgentOutcomeReason.ModelTimeout));

        Assert.Null(result.Response);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, result.HttpStatusCode);
        // O reason interno model_timeout vira o código estável da fronteira.
        Assert.Equal("agent_timeout", result.ErrorCode);
    }

    [Fact]
    public void Unknown_status_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentExecutionApplicationService.MapOutcome(
                Outcome((AgentOutcomeStatus)99, AgentOutcomeReason.Completed)));
    }
}
