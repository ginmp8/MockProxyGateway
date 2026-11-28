var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMockProxyGateway();
var app = builder.Build();
app.UseMockProxyGateway();
app.MapGet("/api/health", () => "normal");
app.Run();
