using Vinx.Erp.Foundation;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapFoundationHealthEndpoints();
app.Run();

public partial class Program;
