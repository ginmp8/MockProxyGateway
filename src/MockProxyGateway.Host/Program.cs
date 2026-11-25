var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMockProxyGateway();
var app = builder.Build();
app.UseMockProxyGateway();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
app.Run();
