using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using Vinx.Erp.Foundation.Observability;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests.Observability;

/// <summary>
/// Valida o gating da exportação OTLP em AddVinxHostDefaults: sem o endpoint
/// configurado nenhum exportador é registrado; com ele, o UseOtlpExporter
/// aparece na coleção de serviços (e as instrumentações continuam registradas).
/// </summary>
public sealed class VinxHostDefaultsTests
{
    private const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    [Fact]
    public void AddVinxHostDefaults_registers_no_otlp_exporter_without_endpoint()
    {
        Environment.SetEnvironmentVariable(OtlpEndpointVariable, null);

        var builder = Host.CreateApplicationBuilder();
        builder.AddVinxHostDefaults();

        // Sanidade: o SDK de métricas está registrado; só o exportador não é.
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(MeterProvider));
        Assert.DoesNotContain(builder.Services, IsOtlpExporterRegistration);
    }

    [Fact]
    public void AddVinxHostDefaults_registers_otlp_exporter_with_endpoint()
    {
        Environment.SetEnvironmentVariable(OtlpEndpointVariable, null);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[OtlpEndpointVariable] = "http://127.0.0.1:4317";
        builder.AddVinxHostDefaults();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(MeterProvider));
        Assert.Contains(builder.Services, IsOtlpExporterRegistration);
    }

    // UseOtlpExporterRegistration é interno ao pacote OTLP e é adicionado à
    // IServiceCollection em toda chamada de UseOtlpExporter; o marcador é
    // reconhecido por nome de tipo para não depender de internals visíveis.
    private static bool IsOtlpExporterRegistration(ServiceDescriptor descriptor)
    {
        const string registrationTypeName = "OpenTelemetry.Exporter.UseOtlpExporterRegistration";
        return descriptor.ServiceType.FullName == registrationTypeName
            || descriptor.ImplementationType?.FullName == registrationTypeName
            || descriptor.ImplementationInstance?.GetType().FullName == registrationTypeName;
    }
}
