using Vinx.Erp.Foundation.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddVinxLogging();
builder.Services.AddOpenTelemetry();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();

public partial class Program;
