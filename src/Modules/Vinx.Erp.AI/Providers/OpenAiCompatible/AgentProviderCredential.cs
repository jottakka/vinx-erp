using System.ClientModel;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Vinx.Erp.AI.UnitTests")]

namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>
/// Credencial sensível do provider, lida exclusivamente do ambiente do processo
/// servidor. Não é bindable por configuração nem expõe o valor original.
/// </summary>
internal sealed class AgentProviderCredential
{
    internal const string EnvironmentVariable = "VINX_AGENT_PROVIDER_API_KEY";

    private readonly string _value;

    private AgentProviderCredential(string value) => _value = value;

    /// <summary>Resolve somente a variável dedicada e rejeita valor vazio ou com controles.</summary>
    internal static AgentProviderCredential? Resolve(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);

        var value = readEnvironment(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            ? null
            : new AgentProviderCredential(value);
    }

    internal ApiKeyCredential ToApiKeyCredential() => new(_value);
}
