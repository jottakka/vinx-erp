namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Declara o tipo do corpo JSON de endpoints que fazem parse manual do request
/// (sem parâmetro tipado). Alimenta somente o documento OpenAPI: o schema é
/// inferido do tipo, sem efeito algum no binding de runtime.
/// </summary>
internal sealed class JsonRequestBodyContract(Type requestType)
{
    public Type RequestType { get; } = requestType;
}
