// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Generic admin info endpoints (version + capabilities). Replaces the version+capabilities
/// pair that previously lived inside the deleted AdminMetadataEndpoints, decoupled from the
/// v1 metadata-resource workflow.
/// </summary>
internal static class AdminInfoEndpoints
{
    public static void MapAdminInfoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin")
            .RequireAdminAuthorization();

        group.MapGet("/version", HandleGetVersion)
            .WithName("GetAdminVersion")
            .WithSummary("Get admin API version info")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<AdminVersionResponse>>();

        group.MapGet("/capabilities", HandleGetCapabilities)
            .WithName("GetAdminCapabilities")
            .WithSummary("Get admin API capabilities")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<AdminCapabilitiesResponse>>();
    }

    private static IResult HandleGetVersion()
    {
        var response = new AdminVersionResponse
        {
            Version = GetServerVersion(),
            MetadataApiVersion = MetadataV2Constants.ApiVersion,
            MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
            ServerTime = DateTimeOffset.UtcNow
        };
        return Results.Json(
            ApiResponse<AdminVersionResponse>.CreateSuccess(response),
            AdminInfoJsonContext.Default.ApiResponseAdminVersionResponse);
    }

    private static IResult HandleGetCapabilities()
    {
        var serverVersion = GetServerVersion();
        var response = new AdminCapabilitiesResponse
        {
            MetadataApiVersion = MetadataV2Constants.ApiVersion,
            MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
            ServerVersion = serverVersion,
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
                Features = new AdminCompatibilityFeatures
                {
                    MetadataResources = true,
                    ManifestExport = true,
                    ManifestApply = true,
                    ManifestDryRun = true,
                    ManifestPrune = true
                },
                AdminApiMajor = AdminApiMajor,
                MetadataApiVersion = MetadataV2Constants.ApiVersion,
                MetadataSchemaVersion = MetadataV2Constants.SchemaVersion
            }
        };
        return Results.Json(
            ApiResponse<AdminCapabilitiesResponse>.CreateSuccess(response),
            AdminInfoJsonContext.Default.ApiResponseAdminCapabilitiesResponse);
    }

    private const string AdminApiMajor = "v1";
    private const int AdminApiMajorNumber = 1;
    private const string AdminApiBasePath = "/api/v1/admin";

    /// <summary>
    /// Derives the SDK <c>releaseChannel</c> from the server's build-version metadata, per
    /// docs/developer/SDK_COMPATIBILITY_METADATA.md ("Release Channel Values"). A version with no
    /// pre-release label is <c>stable</c>; recognized pre-release labels map to their channel; any
    /// other pre-release falls back to <c>dev</c>. SemVer build metadata (after <c>+</c>) is ignored.
    /// </summary>
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

/// <summary>
/// Version metadata returned by the admin version endpoint.
/// </summary>
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
}

/// <summary>
/// Capability and SDK-compatibility metadata returned by the admin capabilities endpoint.
/// </summary>
public sealed record AdminCapabilitiesResponse
{
    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("metadataSchemaVersion")]
    public string MetadataSchemaVersion { get; init; } = string.Empty;

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;

    [JsonPropertyName("compatibility")]
    public AdminCompatibility Compatibility { get; init; } = new();
}

/// <summary>
/// Compatibility contract consumed by the generated admin SDKs to confirm they can talk to this
/// server. Mirrors <c>data.compatibility</c> in docs/developer/SDK_COMPATIBILITY_METADATA.md.
/// </summary>
public sealed record AdminCompatibility
{
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;

    /// <summary>Rollout channel derived from build-version metadata (stable, lts, preview, alpha, ...).</summary>
    [JsonPropertyName("releaseChannel")]
    public string ReleaseChannel { get; init; } = string.Empty;

    /// <summary>Control-plane (admin) API major/base-path/deprecation the SDK gates on first.</summary>
    [JsonPropertyName("controlPlaneApi")]
    public AdminControlPlaneApi ControlPlaneApi { get; init; } = new();

    /// <summary>Supported metadata schema versions, newest non-deprecated first.</summary>
    [JsonPropertyName("metadataSchemas")]
    public IReadOnlyList<AdminMetadataSchema> MetadataSchemas { get; init; } = [];

    /// <summary>Coarse feature switches so SDKs branch without probing endpoints.</summary>
    [JsonPropertyName("features")]
    public AdminCompatibilityFeatures Features { get; init; } = new();

    // Retained flat fields (backward-compatible superset; not part of the canonical envelope).
    [JsonPropertyName("adminApiMajor")]
    public string AdminApiMajor { get; init; } = string.Empty;

    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("metadataSchemaVersion")]
    public string MetadataSchemaVersion { get; init; } = string.Empty;
}

/// <summary>Control-plane (admin) API identity the SDK gates on before constructing admin paths.</summary>
public sealed record AdminControlPlaneApi
{
    [JsonPropertyName("major")]
    public int Major { get; init; }

    [JsonPropertyName("basePath")]
    public string BasePath { get; init; } = string.Empty;

    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}

/// <summary>A supported metadata schema version and whether it is deprecated.</summary>
public sealed record AdminMetadataSchema
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}

/// <summary>Coarse server-owned feature switches advertised in the compatibility handshake.</summary>
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
