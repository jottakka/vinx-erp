using System.Text;
using Vinx.Erp.ApiHost.Agent;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class AgentHttpContractTests
{
    private static readonly Guid RequestId = Guid.Parse("8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    private static readonly Guid CompanyId = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");

    private static MemoryStream Body(string json) => new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Valid_request_without_company_id_parses()
    {
        var ok = AgentHttpContract.TryParse(
            Body("""{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d","journey":"read_only_summarize","message":"resuma o contrato"}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.True(ok);
        Assert.Null(errorCode);
        Assert.Equal(RequestId, request!.RequestId);
        Assert.Equal("read_only_summarize", request.Journey);
        Assert.Equal("resuma o contrato", request.Message);
        Assert.Null(request.CompanyId);
    }

    [Fact]
    public void Valid_request_with_company_id_parses_trimmed_fields()
    {
        var ok = AgentHttpContract.TryParse(
            Body(
                $$"""{"requestId":"{{RequestId}}","journey":"  read_only_summarize  ","message":"  resuma o contrato da Alfa  ","companyId":"{{CompanyId}}"}"""
            ),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.True(ok);
        Assert.Null(errorCode);
        Assert.Equal(RequestId, request!.RequestId);
        Assert.Equal("read_only_summarize", request.Journey);
        Assert.Equal("resuma o contrato da Alfa", request.Message);
        Assert.Equal(CompanyId, request.CompanyId);
    }

    [Fact]
    public void Company_id_null_literal_parses_as_absent()
    {
        var ok = AgentHttpContract.TryParse(
            Body($$"""{"requestId":"{{RequestId}}","journey":"j","message":"m","companyId":null}"""),
            24 * 1024,
            out var request,
            out _
        );

        Assert.True(ok);
        Assert.Null(request!.CompanyId);
    }

    [Fact]
    public void Unknown_field_is_rejected()
    {
        var ok = AgentHttpContract.TryParse(
            Body(
                $$"""{"requestId":"{{RequestId}}","journey":"j","message":"m","companyId":null,"model":"gpt-4"}"""
            ),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    [Theory]
    [InlineData("journey")]
    [InlineData("message")]
    [InlineData("requestId")]
    public void Missing_required_field_is_rejected(string missing)
    {
        var json = missing switch
        {
            "journey" => $$"""{"requestId":"{{RequestId}}","message":"m"}""",
            "message" => $$"""{"requestId":"{{RequestId}}","journey":"j"}""",
            _ => """{"journey":"j","message":"m"}""",
        };

        var ok = AgentHttpContract.TryParse(
            Body(json),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    [Theory]
    [InlineData("""{"requestId":123,"journey":"j","message":"m"}""")]
    [InlineData(
        """{"requestId":"00000000-0000-0000-0000-000000000000","journey":"j","message":"m"}"""
    )]
    [InlineData("""{"requestId":"not-a-guid","journey":"j","message":"m"}""")]
    public void Invalid_request_id_is_rejected(string json)
    {
        var ok = AgentHttpContract.TryParse(Body(json), 24 * 1024, out var request, out var errorCode);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("request_id_invalid", errorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\\n\\t ")]
    public void Empty_message_is_rejected(string message)
    {
        var ok = AgentHttpContract.TryParse(
            Body($$"""{"requestId":"{{RequestId}}","journey":"j","message":"{{message}}"}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("message_empty", errorCode);
    }

    [Fact]
    public void Message_above_limit_is_rejected()
    {
        var message = new string('a', AgentHttpContract.MaxMessageLength + 1);
        var ok = AgentHttpContract.TryParse(
            Body($$"""{"requestId":"{{RequestId}}","journey":"j","message":"{{message}}"}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("message_too_long", errorCode);
    }

    [Fact]
    public void Message_at_limit_with_padding_parses_after_trim()
    {
        var message = $"  {new string('a', AgentHttpContract.MaxMessageLength)}  ";
        var ok = AgentHttpContract.TryParse(
            Body($$"""{"requestId":"{{RequestId}}","journey":"j","message":"{{message}}"}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.True(ok);
        Assert.Null(errorCode);
        Assert.Equal(AgentHttpContract.MaxMessageLength, request!.Message.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jornada-com-muito-mais-de-cem-caracteres-para-exceder-o-limite-estrito-do-campo-journey-no-contrato-do-agente-0001")]
    public void Invalid_journey_is_rejected(string journey)
    {
        var ok = AgentHttpContract.TryParse(
            Body($$"""{"requestId":"{{RequestId}}","journey":"{{journey}}","message":"m"}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("journey_invalid", errorCode);
    }

    [Fact]
    public void Journey_with_wrong_type_is_rejected()
    {
        var ok = AgentHttpContract.TryParse(
            Body("""{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d","journey":42,"message":"m"}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("journey_invalid", errorCode);
    }

    [Fact]
    public void Message_with_wrong_type_is_rejected()
    {
        var ok = AgentHttpContract.TryParse(
            Body("""{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d","journey":"j","message":42}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    [Fact]
    public void Invalid_company_id_is_rejected()
    {
        var ok = AgentHttpContract.TryParse(
            Body("""{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d","journey":"j","message":"m","companyId":"forged"}"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    [Fact]
    public void Body_above_max_bytes_is_rejected()
    {
        var ok = AgentHttpContract.TryParse(
            Body("""{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d","journey":"j","message":"m"}"""),
            16,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    [Fact]
    public void Body_at_exact_limit_parses()
    {
        var json = """{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d","journey":"j","message":"m"}""";
        var ok = AgentHttpContract.TryParse(
            Body(json),
            Encoding.UTF8.GetByteCount(json),
            out var request,
            out var errorCode
        );

        Assert.True(ok);
        Assert.Null(errorCode);
        Assert.Equal("j", request!.Journey);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        var ok = AgentHttpContract.TryParse(
            Body("""{"requestId":"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    [Fact]
    public void Root_array_is_rejected()
    {
        var ok = AgentHttpContract.TryParse(
            Body("""[{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d"}]"""),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    [Fact]
    public void Chunked_stream_counts_effective_bytes()
    {
        var json = $$"""{"requestId":"{{RequestId}}","journey":"j","message":"m"}""";
        var ok = AgentHttpContract.TryParse(
            new SingleByteStream(Encoding.UTF8.GetBytes(json)),
            24 * 1024,
            out var request,
            out var errorCode
        );

        Assert.True(ok);
        Assert.Null(errorCode);
        Assert.Equal(RequestId, request!.RequestId);
    }

    [Fact]
    public void Chunked_stream_above_limit_is_rejected_by_effective_bytes()
    {
        var json = """{"requestId":"8f1d2b3c-4e5f-4a6b-8c7d-9e0f1a2b3c4d","journey":"j","message":"m"}""";
        var ok = AgentHttpContract.TryParse(
            new SingleByteStream(Encoding.UTF8.GetBytes(json)),
            16,
            out var request,
            out var errorCode
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal("invalid_request", errorCode);
    }

    private sealed class SingleByteStream(byte[] payload) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count is 0 || _position >= payload.Length)
            {
                return 0;
            }

            buffer[offset] = payload[_position];
            _position++;
            return 1;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
