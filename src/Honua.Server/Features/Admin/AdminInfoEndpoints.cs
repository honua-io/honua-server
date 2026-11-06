// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Hosting;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static class AdminInfoEndpoints
{
    public static void MapAdminInfoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The capabilities handshake advertises the embedded
        // eng/release/component-versions.v1.json, so refuse to start when that declaration differs from
        // the contract versions this server actually serves.
        ContractVersionsStartupCheck.Validate(
            ComponentVersionsDeclaration.Embedded.ContractVersions,
            ServedContractVersions.Current,
            ProtocolProfile.ExcludedContractNames);

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin")
            .RequireAdminAuthorization();

        group.MapGet("/version", HandleGetVersion)
            .WithName("GetAdminVersion")
            .WithSummary("Get admin API version info")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .Produces<ApiResponse<AdminVersionResponse>>();

        // Anonymous by design (#1633): this is the SDK compatibility handshake
        // (checkCompatibility/getCompatibility) that browser consumers run before they
        // have credentials. The payload is constant, non-sensitive build metadata —
        // server version, release channel, control-plane API major/base path, metadata
        // schema versions, and compile-time feature switches. It contains no connection
        // details, tenants, topology, or deployment-specific configuration.
        group.MapGet("/capabilities", HandleGetCapabilities)
            .WithName("GetAdminCapabilities")
            .WithSummary("Get admin API capabilities")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .Produces<ApiResponse<AdminCapabilitiesResponse>>()
            .AllowAnonymous();
    }

    private static IResult HandleGetVersion([FromServices] IServerInstanceIdentity instance)
    {
        var response = new AdminVersionResponse
        {
            Version = GetServerVersion(),
            MetadataApiVersion = MetadataV2Constants.ApiVersion,
            MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
            ServerTime = DateTimeOffset.UtcNow,
            // Admin-only by design: the anonymous capabilities handshake stays constant build
            // metadata, while this authenticated surface names the replica that answered.
            Instance = new AdminServerInstance
            {
                Id = instance.InstanceId,
                Source = instance.Source,
                StartedAt = instance.StartedAt
            }
        };
        return Results.Json(
            ApiResponse<AdminVersionResponse>.CreateSuccess(response),
            AdminInfoJsonContext.Default.ApiResponseAdminVersionResponse);
    }

    private static IResult HandleGetCapabilities()
    {
        var serverVersion = GetServerVersion();
        var declaration = ComponentVersionsDeclaration.Embedded;
        var response = new AdminCapabilitiesResponse
        {
            MetadataApiVersion = MetadataV2Constants.ApiVersion,
            MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
            ServerVersion = serverVersion,
            ContractVersions = declaration.ContractVersions,
            SchemaVersions = declaration.SchemaVersions,
            // The generated JS/Python/.NET admin SDKs parse this `compatibility` contract from the
            // capabilities response per docs/developer/SDK_COMPATIBILITY_METADATA.md. The documented
            // envelope (controlPlaneApi/releaseChannel/metadataSchemas/features) is the canonical
            // handshake; the flat adminApiMajor/metadata* fields are retained as a backward-compatible
            // superset (adding fields under `compatibility` is a v1-compatible change per the contract).
            Compatibility = new AdminCompatibility
            {
                ServerVersion = serverVersion,
                ReleaseChannel = DeriveReleaseChannel(serverVersion),
                ControlPlaneApi = new AdminControlPlaneApi
                {
                    Major = AdminApiMajorNumber,
                    BasePath = AdminApiBasePath,
                    Deprecated = false
                },
                MetadataSchemas =
                [
                    new AdminMetadataSchema { Version = MetadataV2Constants.ApiVersion, Deprecated = false }
                ],
                // These SDK flags describe the legacy /admin/manifest workflow removed in #1035.
                // Package GitOps export has a different route and payload; its availability cannot
                // advertise support for GET /admin/manifest (#4009).
                Features = new AdminCompatibilityFeatures
                {
                    MetadataResources = true,
                    ManifestExport = false,
                    ManifestApply = false,
                    ManifestDryRun = false,
                    ManifestPrune = false
                },
                AdminApiMajor = AdminApiMajor,
                MetadataApiVersion = MetadataV2Constants.ApiVersion,
                MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
                // The release-train contract-live gate reads this map as the whole
                // advertised contract set, keyed like eng/release/component-versions.v1.json.
                ContractVersions = declaration.ContractVersions,
                SchemaVersions = declaration.SchemaVersions
            }
        };
        return Results.Json(
            ApiResponse<AdminCapabilitiesResponse>.CreateSuccess(response),
            AdminInfoJsonContext.Default.ApiResponseAdminCapabilitiesResponse);
    }

    internal const string AdminApiMajor = "v1";
    private const int AdminApiMajorNumber = 1;
    private const string AdminApiBasePath = "/api/v1/admin";

    private static string DeriveReleaseChannel(string version)
    {
        var buildMetadata = version.IndexOf('+', StringComparison.Ordinal);
        var core = buildMetadata >= 0 ? version[..buildMetadata] : version;

        var preReleaseStart = core.IndexOf('-', StringComparison.Ordinal);
        if (preReleaseStart < 0)
        {
            return "stable";
        }

        var preRelease = core[(preReleaseStart + 1)..].ToLowerInvariant();
        if (preRelease.Contains("nightly", StringComparison.Ordinal))
        {
            return "nightly";
        }
        if (preRelease.Contains("preview", StringComparison.Ordinal))
        {
            return "preview";
        }
        if (preRelease.Contains("alpha", StringComparison.Ordinal))
        {
            return "alpha";
        }
        if (preRelease.Contains("beta", StringComparison.Ordinal))
        {
            return "beta";
        }
        if (preRelease.Contains("lts", StringComparison.Ordinal))
        {
            return "lts";
        }
        if (preRelease.StartsWith("rc", StringComparison.Ordinal))
        {
            return "rc";
        }

        return "dev";
    }

    private static string GetServerVersion()
    {
        var asm = typeof(AdminInfoEndpoints).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() ?? "0.0.0" : info;
    }
}

public sealed record AdminVersionResponse
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("metadataSchemaVersion")]
    public string MetadataSchemaVersion { get; init; } = string.Empty;

    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; init; }

    [JsonPropertyName("instance")]
    public AdminServerInstance? Instance { get; init; }
}

public sealed record AdminServerInstance
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }
}

public sealed record AdminCapabilitiesResponse
{
    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("metadataSchemaVersion")]
    public string MetadataSchemaVersion { get; init; } = string.Empty;

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;

    [JsonPropertyName("contractVersions")]
    public IReadOnlyDictionary<string, string> ContractVersions { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("schemaVersions")]
    public IReadOnlyDictionary<string, string> SchemaVersions { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("compatibility")]
    public AdminCompatibility Compatibility { get; init; } = new();
}

public sealed record AdminCompatibility
{
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;

    [JsonPropertyName("releaseChannel")]
    public string ReleaseChannel { get; init; } = string.Empty;

    [JsonPropertyName("controlPlaneApi")]
    public AdminControlPlaneApi ControlPlaneApi { get; init; } = new();

    [JsonPropertyName("metadataSchemas")]
    public IReadOnlyList<AdminMetadataSchema> MetadataSchemas { get; init; } = [];

    [JsonPropertyName("features")]
    public AdminCompatibilityFeatures Features { get; init; } = new();

    // Retained flat fields (backward-compatible superset; not part of the canonical envelope).
    [JsonPropertyName("adminApiMajor")]
    public string AdminApiMajor { get; init; } = string.Empty;

    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("metadataSchemaVersion")]
    public string MetadataSchemaVersion { get; init; } = string.Empty;

    // Startup refuses a contract-version declaration that differs from the versions this server serves, so this map is the whole advertised set.
    [JsonPropertyName("contractVersions")]
    public IReadOnlyDictionary<string, string> ContractVersions { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("schemaVersions")]
    public IReadOnlyDictionary<string, string> SchemaVersions { get; init; } = new Dictionary<string, string>();
}

public sealed record AdminControlPlaneApi
{
    [JsonPropertyName("major")]
    public int Major { get; init; }

    [JsonPropertyName("basePath")]
    public string BasePath { get; init; } = string.Empty;

    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}

public sealed record AdminMetadataSchema
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}

public sealed record AdminCompatibilityFeatures
{
    [JsonPropertyName("metadataResources")]
    public bool MetadataResources { get; init; }

    [JsonPropertyName("manifestExport")]
    public bool ManifestExport { get; init; }

    [JsonPropertyName("manifestApply")]
    public bool ManifestApply { get; init; }

    [JsonPropertyName("manifestDryRun")]
    public bool ManifestDryRun { get; init; }

    [JsonPropertyName("manifestPrune")]
    public bool ManifestPrune { get; init; }
}

[JsonSerializable(typeof(ApiResponse<AdminVersionResponse>))]
[JsonSerializable(typeof(ApiResponse<AdminCapabilitiesResponse>))]
internal sealed partial class AdminInfoJsonContext : JsonSerializerContext
{
}
