---
type: reference
title: "Get started with the .NET SDK"
description: "Install the Honua .NET SDK, point a client at your server, authenticate with an API key, and make your first feature query."
resource: "https://github.com/orgs/honua-io/packages?repo_name=honua-sdk-dotnet"
---
# Get started with the .NET SDK

Install the Honua .NET SDK, point a client at your server, authenticate with an API key, and make your first feature query.

**Prerequisites:** A running Honua server ([quickstart](../../get-started/quickstart.md)) with at least one published layer ([publish layers](../../guides/publish/publish-layers.md)), the .NET 10 SDK, and an API key (see [Authenticate clients](../../guides/secure/authentication.md) — the SDK landing page shows how to [mint a scoped key](../README.md#authentication)).

The .NET SDK ships as `Honua.Sdk` — an umbrella package over a family of `Honua.Sdk.*` libraries (`Honua.Sdk.Grpc`, `Honua.Sdk.Admin`, `Honua.Sdk.GeoServices`, `Honua.Sdk.Catalogs`, and more). It is built for dependency injection. The current published release is **1.10.3**, targeting **net10.0**. `Host.CreateApplicationBuilder` lives in `Microsoft.Extensions.Hosting` 10.0.0. `Honua.Sdk` does not reference that package, so add it beside the SDK.

## Steps

### 1. Install the package

```bash
dotnet add package Honua.Sdk --version 1.10.3
dotnet add package Microsoft.Extensions.Hosting --version 10.0.0
```

Every `Honua.Sdk*` package is on [nuget.org](https://www.nuget.org/packages/Honua.Sdk/) and
installs anonymously - no feed to add, no token to mint. The same builds are also mirrored to
GitHub Packages, which requires a PAT even for public packages; prefer nuget.org unless you
have a specific reason not to.

The umbrella package pulls in the per-protocol clients. If you only need one surface — for example the gRPC feature client — you can reference it directly instead (`dotnet add package Honua.Sdk.Grpc`).

### 2. Register a client

`Honua.Sdk` integrates with the .NET service container. Call `AddHonua` and set the base address and credentials. The SDK sends the API key on every request:

```csharp
using Honua.Sdk;                  // AddHonua
using Honua.Sdk.Grpc;             // IHonuaGrpcClient
using Honua.Sdk.Grpc.Extensions;  // AddHonuaGrpc
using Honua.Sdk.Grpc.Models;      // QueryFeaturesRequest, QueryFeaturesResponse
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHonua(options =>
{
    options.BaseAddress = new Uri("http://localhost:8080");
    options.ApiKey = Environment.GetEnvironmentVariable("HONUA_API_KEY");
    // For OIDC instead of an API key, set options.BearerToken or
    // options.BearerTokenProvider = ct => tokenCache.GetAccessTokenAsync(ct);
});

// gRPC is a separate h2c listener on 8081, so it takes its own address.
// AddHonua's BaseAddress feeds every sub-client, and 8080 speaks HTTP/1.1.
builder.Services.AddHonuaGrpc(options =>
{
    options.BaseAddress = new Uri("http://localhost:8081");
    options.ApiKey = Environment.GetEnvironmentVariable("HONUA_API_KEY");
});

using var host = builder.Build();
```

`ApiKey` is sent as the `X-API-Key` header. The options also accept `ApiKeyProvider` and `BearerTokenProvider` delegates if you resolve credentials dynamically.

> **gRPC listens on a different port.** The HTTP protocols are on 8080; gRPC is HTTP/2 cleartext
> (h2c) on **8081** (`Kestrel:Endpoints:Grpc:Url`, exposed as `HONUA_GRPC_PORT` in Compose).
> Pointing a gRPC client at 8080 fails at runtime with `HTTP_1_1_REQUIRED`. See
> [the gRPC protocol reference](../../reference/protocols/grpc.md).

> **Ports on this page** are the repository Compose defaults. If you changed them in the
> [quickstart](../../get-started/quickstart.md), substitute your own throughout.

### 3. Make your first call

Resolve a client from the container and query a published layer. This uses the gRPC feature client. Set `HONUA_SERVICE` and `HONUA_LAYER_ID` to a feature layer you published — the [quickstart](../../get-started/quickstart.md) prints the service name, and [the first dataset](../../get-started/first-dataset.md) exports both. Not every layer has a `name` attribute, so a missing value prints blank instead of throwing.

```bash
export HONUA_SERVICE="<your-service>"
export HONUA_LAYER_ID="<your-layer-id>"
```

```csharp
var serviceId = Environment.GetEnvironmentVariable("HONUA_SERVICE")
    ?? throw new InvalidOperationException("Set HONUA_SERVICE to a published service name.");
var layerId = int.Parse(Environment.GetEnvironmentVariable("HONUA_LAYER_ID")
    ?? throw new InvalidOperationException("Set HONUA_LAYER_ID to that service's layer id."));

var grpc = host.Services.GetRequiredService<IHonuaGrpcClient>();

var response = await grpc.QueryFeaturesAsync(new QueryFeaturesRequest
{
    ServiceId      = serviceId,
    LayerId        = layerId,
    Where          = "1=1",
    OutFields      = new[] { "*" },
    ReturnGeometry = true,
});

foreach (var feature in response.Features)
{
    feature.Attributes.TryGetValue("name", out var name);
    Console.WriteLine($"{feature.Id}: {name}");
}
```

## Verify

Print how many features came back:

```csharp
Console.WriteLine($"Returned {response.Features.Count} features.");
```

A wrong or missing API key surfaces as an unauthenticated error from the client. Confirm `HONUA_API_KEY` is set, then rerun the authenticated SDK query above.

## Available clients

`AddHonua` can register the per-protocol clients you need; resolve them from the container:

| Client interface | Registered by | Use it for |
|---|---|---|
| `IHonuaGrpcClient` | `AddHonua()` / `AddHonuaGrpc()` | Streaming feature queries over gRPC |
| `IHonuaAdminClient` | `AddHonua()` / `AddHonuaAdmin()` | Control plane — connections, imports, layers, keys |
| `IHonuaFeatureServerClient` | `AddHonuaFeatureServer()` / `AddHonua(o => o.UseGeoServices = true)` | ArcGIS-style FeatureServer queries |
| `IHonuaStacClient` | `AddHonuaStac()` / `AddHonua(o => o.UseStac = true)` | STAC collections and item search |

## Troubleshoot

| Symptom | Fix |
|---|---|
| Unauthenticated / 401 from the client | `ApiKey` is unset or wrong; set it and rerun the authenticated SDK query above. |
| `IHonuaFeatureServerClient` / `IHonuaStacClient` not resolvable | Enable the surface — `AddHonua(o => { o.UseGeoServices = true; o.UseStac = true; })` or call the per-package `AddHonua*` method. |
| Empty result set | The `Where` clause filtered everything out, or the layer is empty; try `Where = "1=1"` and check the layer in [the console](../../concepts/ecosystem.md) or via the HTTP API. |

More general failures: [Troubleshooting](../../guides/deploy/troubleshooting.md).

## Next steps

- [.NET common tasks](dotnet-common-tasks.md) — query a FeatureServer layer and run a STAC search
- [honua-sdk-dotnet on GitHub](https://github.com/honua-io/honua-sdk-dotnet) — full package list and samples
- [Query features over HTTP](../../guides/query-analyze/query-features.md) — the protocol surfaces the SDK wraps
- [SDK overview](../README.md)
