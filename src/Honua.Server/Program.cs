using Honua.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Add Aspire service defaults (OTel, health, resilience)
builder.AddServiceDefaults();

// Add Npgsql with connection from Aspire
builder.AddNpgsqlDataSource("honua");

// Add Redis if configured
builder.AddRedisDistributedCache("redis");

var app = builder.Build();

// Map health endpoints for Aspire dashboard
app.MapDefaultEndpoints();

app.MapGet("/", () => "Hello World from Honua Server!");
app.MapGet("/healthz", () => "Healthy");

app.Run();