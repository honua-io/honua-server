---
type: reference
title: "Get started with the .NET SDK"
description: "Install the Honua .NET SDK, point a client at your server, authenticate with an API key, and make your first feature query."
resource: "https://github.com/orgs/honua-io/packages?repo_name=honua-sdk-dotnet"
---
# Get started with the .NET SDK

Install the Honua .NET SDK, point a client at your server, authenticate with an API key, and make your first feature query.

**Prerequisites:** A running Honua server ([quickstart](../../get-started/quickstart.md)) with at least one published layer ([publish layers](../../guides/publish/publish-layers.md)), the .NET 10 SDK, and an API key (see [Authenticate clients](../../guides/secure/authentication.md) — the SDK landing page shows how to [mint a scoped key](../README.md#authentication)).

The .NET SDK ships as `Honua.Sdk` — an umbrella package over a family of `Honua.Sdk.*` libraries (`Honua.Sdk.Grpc`, `Honua.Sdk.Admin`, `Honua.Sdk.GeoServices`, `Honua.Sdk.Catalogs`, and more). It is built for dependency injection and `Microsoft.Extensions.Hosting`. The current published release is **1.6.0**, targeting **net10.0**.

## Steps

### 1. Install the package

> **`Honua.Sdk*` is not on nuget.org yet.** It is published to GitHub Packages, which requires
> authentication even for public packages, so a bare `dotnet add package Honua.Sdk` fails with
> `NU1101`. Add the feed first. See
> [honua-sdk-dotnet INSTALL.md](https://github.com/honua-io/honua-sdk-dotnet/blob/trunk/INSTALL.md)
> for the full path, including PAT scopes.

```bash
dotnet nuget add source "https://nuget.pkg.github.com/honua-io/index.json" \
  --name honua \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text

dotnet add package Honua.Sdk --version 1.6.0 \
  --source "https://nuget.pkg.github.com/honua-io/index.json"
```

Pass `--version` explicitly: an unversioned `dotnet add package` against this feed reports
`There are no versions available for the package`, even when the feed is correctly
authenticated and populated. Prefer the full feed URL over the `--name honua` alias, which can
degrade to a filesystem-path lookup (`NU1301`) within a session.

The umbrella package pulls in the per-protocol clients. If you only need one surface — for example the gRPC feature client — you can reference it directly instead (`dotnet add package Honua.Sdk.Grpc --version 1.6.0`).

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

> **gRPC listens on a different port.** The HTTP protocols are on 8080; gRPC is HTTP/2 cleartext > (h2c) on **8081** (`Kestrel:Endpoints:Grpc:Url`, exposed as `HONUA_GRPC_PORT` in Compose). > Pointing a gRPC client at 8080 fails at runtime with `HTTP_1_1_REQUIRED`. See > [the gRPC protocol reference](../../reference/protocols/grpc.md).

> **Ports on this page** are the repository Compose defaults. If you changed them in the > [quickstart](../../get-started/quickstart.md), substitute your own throughout.

### 3. Make your first call

Resolve a client from the container and query a published layer. This uses the gRPC feature client; swap `serviceId`/`layerId` for one of your own layers:

```csharp
var grpc = host.Services.GetRequiredService<IHonuaGrpcClient>();

var response = await grpc.QueryFeaturesAsync(new QueryFeaturesRequest
{
    ServiceId      = "default",
    LayerId        = 0,
    Where          = "1=1",
    OutFields      = new[] { "*" },
    ReturnGeometry = true,
});

foreach (var feature in response.Features)
{
    Console.WriteLine($"{feature.Id}: {feature.Attributes["name"]}");
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

- [.NET common tasks](common-tasks.md) — query a FeatureServer layer and run a STAC search
- [honua-sdk-dotnet on GitHub](https://github.com/honua-io/honua-sdk-dotnet) — full package list and samples
- [Query features over HTTP](../../guides/query-analyze/query-features.md) — the protocol surfaces the SDK wraps
- [SDK overview](../README.md)
