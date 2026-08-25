using System.Net;
using System.Text.Json;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Pins complementares dos invariantes de contrato que a inferência passou a
/// garantir: media type único, schemas fechados (additionalProperties:false),
/// formatos UUID e limites de tamanho. Arquivo novo — os testes pinados
/// existentes permanecem sem edição (oráculo da refatoração).
/// </summary>
public sealed class OpenApiInferenceContractPinTests
{
    [Fact]
    public async Task Agent_run_exposes_optional_bounded_uuid_attachments()
    {
        using var document = await LoadDocumentAsync();
        var schema = RequestSchemaOf(document.RootElement.GetProperty("paths"), "/agent/runs");

        Assert.Equal(["journey", "message", "requestId"], RequiredOf(schema).Order());
        AssertClosedObject(schema);
        var attachments = schema.GetProperty("properties").GetProperty("attachments");
        Assert.Equal("array", attachments.GetProperty("type").GetString());
        Assert.Equal(3, attachments.GetProperty("maxItems").GetInt32());
        AssertUuid(attachments.GetProperty("items"));
    }

    [Fact]
    public async Task Paguai_create_pins_the_sole_json_media_type_and_the_inferred_request_schema()
    {
        using var document = await LoadDocumentAsync();
        var requestBody = document.RootElement
            .GetProperty("paths")
            .GetProperty("/paguai/requests")
            .GetProperty("post")
            .GetProperty("requestBody");

        Assert.True(requestBody.GetProperty("required").GetBoolean());
        Assert.Equal(
            ["application/json"],
            MediaTypesOf(requestBody).Order()
        );

        var schema = JsonSchemaOf(requestBody);
        Assert.Equal(
            [
                "amount",
                "beneficiaryDisplayName",
                "companyId",
                "currency",
                "description",
                "dueDate",
                "proofRequirement",
                "requestId",
                "title",
            ],
            RequiredOf(schema).Order()
        );
        AssertClosedObject(schema);
        AssertUuid(schema.GetProperty("properties").GetProperty("companyId"));
        AssertUuid(schema.GetProperty("properties").GetProperty("requestId"));
        var title = schema.GetProperty("properties").GetProperty("title");
        Assert.Equal(1, title.GetProperty("minLength").GetInt32());
        Assert.Equal(200, title.GetProperty("maxLength").GetInt32());
        var description = schema.GetProperty("properties").GetProperty("description");
        Assert.Equal(1, description.GetProperty("minLength").GetInt32());
        Assert.Equal(4000, description.GetProperty("maxLength").GetInt32());
        var currency = schema.GetProperty("properties").GetProperty("currency");
        Assert.Equal(new[] { "BRL" }, EnumOf(currency));
        var proofRequirement = schema.GetProperty("properties").GetProperty("proofRequirement");
        Assert.Equal(new[] { "optional", "required" }, EnumOf(proofRequirement));
        var amount = schema.GetProperty("properties").GetProperty("amount");
        Assert.Equal("string", amount.GetProperty("type").GetString());
        Assert.Equal(
            "^\\d+(?:\\.\\d{1,2})?$",
            amount.GetProperty("pattern").GetString()
        );
        Assert.Equal(9_999_999_999_999_999.99m, amount.GetProperty("maximum").GetDecimal());
        Assert.Equal(0.01m, amount.GetProperty("minimum").GetDecimal());
    }

    [Fact]
    public async Task Paguai_response_schema_pins_the_decimal_string_amount()
    {
        using var document = await LoadDocumentAsync();
        var created = document.RootElement
            .GetProperty("paths")
            .GetProperty("/paguai/requests")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("#/components/schemas/PaguaiDraft", created.GetProperty("$ref").GetString());

        var draft = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PaguaiDraft");
        var amount = draft.GetProperty("properties").GetProperty("amount");
        Assert.Equal("string", amount.GetProperty("type").GetString());
        Assert.Equal(
            "^\\d+\\.\\d{2}$",
            amount.GetProperty("pattern").GetString()
        );
        Assert.Equal(9_999_999_999_999_999.99m, amount.GetProperty("maximum").GetDecimal());
        Assert.Equal(0.01m, amount.GetProperty("minimum").GetDecimal());
    }

    [Fact]
    public async Task Sign_in_pins_a_closed_request_schema()
    {
        using var document = await LoadDocumentAsync();
        var requestBody = document.RootElement
            .GetProperty("paths")
            .GetProperty("/foundation/identity/browser-session")
            .GetProperty("post")
            .GetProperty("requestBody");

        Assert.Equal(
            ["application/json"],
            MediaTypesOf(requestBody).Order()
        );

        var schema = JsonSchemaOf(requestBody);
        Assert.Equal(
            ["email", "password"],
            RequiredOf(schema).Order()
        );
        AssertClosedObject(schema);
        Assert.Equal(
            "string",
            schema.GetProperty("properties").GetProperty("email").GetProperty("type").GetString()
        );
        Assert.Equal(
            "string",
            schema.GetProperty("properties").GetProperty("password").GetProperty("type").GetString()
        );
    }

    [Fact]
    public async Task Employee_create_pins_uuid_formats_and_the_created_response_reference()
    {
        using var document = await LoadDocumentAsync();
        var post = document.RootElement
            .GetProperty("paths")
            .GetProperty("/foundation/companies/{companyId}/employees")
            .GetProperty("post");

        var requestSchema = JsonSchemaOf(post.GetProperty("requestBody"));
        Assert.Equal(
            ["companyId", "displayName", "email", "requestId"],
            RequiredOf(requestSchema).Order()
        );
        AssertClosedObject(requestSchema);
        AssertUuid(requestSchema.GetProperty("properties").GetProperty("requestId"));
        AssertUuid(requestSchema.GetProperty("properties").GetProperty("companyId"));
        var email = requestSchema.GetProperty("properties").GetProperty("email");
        Assert.Equal("string", email.GetProperty("type").GetString());
        Assert.Equal("email", email.GetProperty("format").GetString());

        var responseSchema = post.GetProperty("responses")
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal(
            "#/components/schemas/EmployeeDirectoryCreatedResponse",
            responseSchema.GetProperty("$ref").GetString()
        );

        var created = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("EmployeeDirectoryCreatedResponse");
        Assert.Equal(
            ["employeeId", "membershipId", "userId"],
            RequiredOf(created).Order()
        );
        AssertClosedObject(created);
        AssertUuid(created.GetProperty("properties").GetProperty("employeeId"));
        AssertUuid(created.GetProperty("properties").GetProperty("membershipId"));
        AssertUuid(created.GetProperty("properties").GetProperty("userId"));
    }

    private static readonly string[] sourceArray = new[] { "companyId", "column", "expectedVersion", "requestId" };

    [Fact]
    public async Task Atendimento_mutations_pin_the_inferred_request_contracts()
    {
        using var document = await LoadDocumentAsync();
        var paths = document.RootElement.GetProperty("paths");

        var create = RequestSchemaOf(paths, "/atendimento/cards");
        Assert.Equal(
            ["companyId", "description", "requestId", "title"],
            RequiredOf(create).Order()
        );
        AssertClosedObject(create);
        AssertUuid(create.GetProperty("properties").GetProperty("companyId"));
        AssertUuid(create.GetProperty("properties").GetProperty("requestId"));
        Assert.Equal(
            200,
            create.GetProperty("properties")
                .GetProperty("title")
                .GetProperty("maxLength")
                .GetInt32()
        );
        Assert.Equal(
            4000,
            create.GetProperty("properties")
                .GetProperty("description")
                .GetProperty("maxLength")
                .GetInt32()
        );

        var assign = RequestSchemaOf(paths, "/atendimento/cards/{id}/assign");
        Assert.Equal(
            ["assigneeMembershipId", "companyId", "expectedVersion", "requestId"],
            RequiredOf(assign).Order()
        );
        AssertClosedObject(assign);
        AssertUuid(assign.GetProperty("properties").GetProperty("companyId"));
        AssertUuid(assign.GetProperty("properties").GetProperty("requestId"));
        AssertUuid(assign.GetProperty("properties").GetProperty("assigneeMembershipId"));
        AssertExpectedVersionMinimumOfOne(assign);

        var move = RequestSchemaOf(paths, "/atendimento/cards/{id}/move");
        Assert.Equal(
sourceArray.Order(),
            RequiredOf(move).Order()
        );
        AssertClosedObject(move);
        AssertUuid(move.GetProperty("properties").GetProperty("companyId"));
        AssertUuid(move.GetProperty("properties").GetProperty("requestId"));
        var column = move.GetProperty("properties").GetProperty("column");
        Assert.Equal(1, column.GetProperty("minLength").GetInt32());
        Assert.Equal(30, column.GetProperty("maxLength").GetInt32());
        AssertExpectedVersionMinimumOfOne(move);
    }

    private static async Task<JsonDocument> LoadDocumentAsync()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<string> MediaTypesOf(JsonElement requestBody)
    {
        foreach (var mediaType in requestBody.GetProperty("content").EnumerateObject())
        {
            yield return mediaType.Name;
        }
    }

    private static JsonElement RequestSchemaOf(JsonElement paths, string path)
    {
        var requestBody = paths.GetProperty(path).GetProperty("post").GetProperty("requestBody");
        Assert.Equal(
            ["application/json"],
            MediaTypesOf(requestBody).Order()
        );
        return JsonSchemaOf(requestBody);
    }

    private static JsonElement JsonSchemaOf(JsonElement requestBody) =>
        requestBody.GetProperty("content").GetProperty("application/json").GetProperty("schema");

    private static IEnumerable<string> RequiredOf(JsonElement schema)
    {
        foreach (var entry in schema.GetProperty("required").EnumerateArray())
        {
            yield return entry.GetString()!;
        }
    }

    private static IEnumerable<string> EnumOf(JsonElement property)
    {
        foreach (var entry in property.GetProperty("enum").EnumerateArray())
        {
            yield return entry.GetString()!;
        }
    }

    private static void AssertClosedObject(JsonElement schema)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    private static void AssertUuid(JsonElement property)
    {
        Assert.Equal("string", property.GetProperty("type").GetString());
        Assert.Equal("uuid", property.GetProperty("format").GetString());
    }

    private static void AssertExpectedVersionMinimumOfOne(JsonElement schema) =>
        Assert.Equal(
            1L,
            schema.GetProperty("properties")
                .GetProperty("expectedVersion")
                .GetProperty("minimum")
                .GetInt64()
        );
}
