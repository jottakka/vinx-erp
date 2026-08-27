using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

public sealed class PaguaiDocumentProposalParserTests
{
    private static readonly Guid FileId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void Parse_returns_only_the_typed_safe_proposal_for_a_closed_authorized_envelope()
    {
        var result = PaguaiDocumentProposalParser.Parse(Request(
            """
            {
              "status": "proposal",
              "reason": null,
              "proposal": {
                "title": "Fatura de energia",
                "description": "Pagamento mensal",
                "amount": "250.00",
                "currency": "BRL",
                "dueDate": "2026-09-10",
                "beneficiaryDisplayName": "Companhia de energia",
                "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111",
                "confidence": 0.80,
                "sourceReferences": [
                  { "field": "amount", "source": "11111111111141118111111111111111:p1" },
                  { "field": "dueDate", "source": "11111111111141118111111111111111:p1" },
                  { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }
                ]
              }
            }
            """));

        Assert.Equal(PaguaiProposalParseStatus.Proposal, result.Status);
        Assert.Null(result.Reason);
        var proposal = Assert.IsType<PaguaiDocumentProposal>(result.Proposal);
        Assert.Equal(250.00m, proposal.Amount);
        Assert.Equal(new DateOnly(2026, 9, 10), proposal.DueDate);
        Assert.Equal(FileId, proposal.InvoiceFileId);
        Assert.Equal(0.80d, proposal.Confidence);
        Assert.Equal(3, proposal.SourceReferences.Count);
        Assert.DoesNotContain("raw-ocr", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidClosedEnvelopes))]
    public void InvalidClosedEnvelopes_are_withheld(string structuredJson, PaguaiProposalParseStatus expectedStatus, string expectedReason)
    {
        var result = PaguaiDocumentProposalParser.Parse(Request(structuredJson));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Proposal);
        Assert.DoesNotContain("raw-ocr", result.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, PaguaiProposalParseStatus, string> InvalidClosedEnvelopes => new()
    {
        {
            """
            { "status": "proposal", "reason": null, "proposal": null }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            { "status": "proposal", "reason": null, "proposal": {}, "unexpected": "raw-ocr" }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            { "status": "proposal", "reason": null, "proposal": "not-an-object" }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": 250.00, "currency": "BRL",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.001", "currency": "BRL",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.00", "currency": "USD",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.00", "currency": "BRL",
                "dueDate": "2026-09-31", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.00", "currency": "BRL",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "automatic",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.00", "currency": "BRL",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "22222222-2222-4222-8222-222222222222", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.Invalid,
            "schema_invalid"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.00", "currency": "BRL",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.NeedsClarification,
            "insufficient_evidence"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.00", "currency": "BRL",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.90,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p2" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.NeedsClarification,
            "insufficient_evidence"
        },
        {
            """
            {
              "status": "proposal", "reason": null,
              "proposal": {
                "title": "Fatura", "description": "Pagamento", "amount": "250.00", "currency": "BRL",
                "dueDate": "2026-09-10", "beneficiaryDisplayName": "Energia", "proofRequirement": "optional",
                "invoiceFileId": "11111111-1111-4111-8111-111111111111", "confidence": 0.79,
                "sourceReferences": [{ "field": "amount", "source": "11111111111141118111111111111111:p1" }, { "field": "dueDate", "source": "11111111111141118111111111111111:p1" }, { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }]
              }
            }
            """,
            PaguaiProposalParseStatus.NeedsClarification,
            "low_confidence"
        },
        {
            """
            { "status": "needs_clarification", "reason": "not_financial_document", "proposal": null }
            """,
            PaguaiProposalParseStatus.NeedsClarification,
            "not_financial_document"
        },
        {
            """
            { "status": "needs_clarification", "reason": "insufficient_evidence", "proposal": null }
            """,
            PaguaiProposalParseStatus.NeedsClarification,
            "insufficient_evidence"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidProposalTextValues))]
    public void Proposal_rejects_empty_whitespace_and_oversized_text_fields(string structuredJson)
    {
        var result = PaguaiDocumentProposalParser.Parse(Request(structuredJson));

        Assert.Equal(PaguaiProposalParseStatus.Invalid, result.Status);
        Assert.Equal("schema_invalid", result.Reason);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public void Proposal_rejects_empty_expected_and_invoice_file_ids()
    {
        var result = PaguaiDocumentProposalParser.Parse(Request(ValidProposalJson.Replace(
            "\"invoiceFileId\": \"11111111-1111-4111-8111-111111111111\"",
            "\"invoiceFileId\": \"00000000-0000-0000-0000-000000000000\"",
            StringComparison.Ordinal), Guid.Empty));

        Assert.Equal(PaguaiProposalParseStatus.Invalid, result.Status);
        Assert.Equal("schema_invalid", result.Reason);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public void Proposal_requires_exactly_one_authorized_source_for_each_financial_field()
    {
        var structuredJson = ValidProposalJson.Replace(
            "{ \"field\": \"beneficiaryDisplayName\", \"source\": \"11111111111141118111111111111111:p1\" }",
            "{ \"field\": \"beneficiaryDisplayName\", \"source\": \"11111111111141118111111111111111:p1\" }, { \"field\": \"amount\", \"source\": \"11111111111141118111111111111111:p1\" }",
            StringComparison.Ordinal);

        var result = PaguaiDocumentProposalParser.Parse(Request(structuredJson));

        Assert.Equal(PaguaiProposalParseStatus.NeedsClarification, result.Status);
        Assert.Equal("insufficient_evidence", result.Reason);
        Assert.Null(result.Proposal);
    }

    public static TheoryData<string> InvalidProposalTextValues => new()
    {
        ValidProposalJson.Replace("\"title\": \"Fatura de energia\"", "\"title\": \"\"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"title\": \"Fatura de energia\"", "\"title\": \"   \"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"title\": \"Fatura de energia\"", $"\"title\": \"{new string('t', 201)}\"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"description\": \"Pagamento mensal\"", "\"description\": \"\"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"description\": \"Pagamento mensal\"", "\"description\": \"   \"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"description\": \"Pagamento mensal\"", $"\"description\": \"{new string('d', 4_001)}\"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"beneficiaryDisplayName\": \"Companhia de energia\"", "\"beneficiaryDisplayName\": \"\"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"beneficiaryDisplayName\": \"Companhia de energia\"", "\"beneficiaryDisplayName\": \"   \"", StringComparison.Ordinal),
        ValidProposalJson.Replace("\"beneficiaryDisplayName\": \"Companhia de energia\"", $"\"beneficiaryDisplayName\": \"{new string('b', 201)}\"", StringComparison.Ordinal),
    };

    private const string ValidProposalJson = """
        {
          "status": "proposal",
          "reason": null,
          "proposal": {
            "title": "Fatura de energia",
            "description": "Pagamento mensal",
            "amount": "250.00",
            "currency": "BRL",
            "dueDate": "2026-09-10",
            "beneficiaryDisplayName": "Companhia de energia",
            "proofRequirement": "optional",
            "invoiceFileId": "11111111-1111-4111-8111-111111111111",
            "confidence": 0.80,
            "sourceReferences": [
              { "field": "amount", "source": "11111111111141118111111111111111:p1" },
              { "field": "dueDate", "source": "11111111111141118111111111111111:p1" },
              { "field": "beneficiaryDisplayName", "source": "11111111111141118111111111111111:p1" }
            ]
          }
        }
        """;

    private static PaguaiDocumentProposalParseRequest Request(string structuredJson, Guid? fileRecordId = null) => new(
        fileRecordId ?? FileId,
        new HashSet<string>(StringComparer.Ordinal)
        {
            "11111111111141118111111111111111:p1",
        },
        structuredJson);
}
