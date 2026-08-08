using Vinx.Erp.Foundation;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenTelemetry();

var app = builder.Build();
app.MapFoundationHealthEndpoints();
app.Run();

public partial class Program;
