var builder = WebApplication.CreateBuilder(args);

// TODO: Add services

var app = builder.Build();

app.MapGet("/healthz/live", () => Results.Ok("Healthy"));
app.MapGet("/healthz/ready", () => Results.Ok("Ready"));

app.Run();

// Make Program accessible to WebApplicationFactory
public partial class Program { }
