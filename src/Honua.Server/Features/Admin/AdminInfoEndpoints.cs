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
            // capabilities response (serverVersion is required by all three; the admin API major lets
            // the .NET SDK's CheckCompatibilityAsync confirm the server speaks admin API v1).
            Compatibility = new AdminCompatibility
            {
                ServerVersion = serverVersion,
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
}

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
/// Compatibility contract consumed by the generated admin SDKs to confirm they can talk to this server.
/// </summary>
public sealed record AdminCompatibility
{
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;

    [JsonPropertyName("adminApiMajor")]
    public string AdminApiMajor { get; init; } = string.Empty;

    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("metadataSchemaVersion")]
    public string MetadataSchemaVersion { get; init; } = string.Empty;
}

[JsonSerializable(typeof(ApiResponse<AdminVersionResponse>))]
[JsonSerializable(typeof(ApiResponse<AdminCapabilitiesResponse>))]
internal sealed partial class AdminInfoJsonContext : JsonSerializerContext
{
}
