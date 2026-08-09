using Vinx.Erp.Foundation;
using Vinx.Erp.Foundation.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddVinxLogging();
builder.Services.AddOpenTelemetry();

var app = builder.Build();
app.UseVinxRequestCorrelation();
app.MapFoundationHealthEndpoints();
app.Run();

public partial class Program;
