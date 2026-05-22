// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;

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

    private static IResult HandleGetVersion(HttpContext context, ILoggerFactory loggerFactory)
    {
        try
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
        catch (Exception ex)
        {
            return HandleAdminInfoFailure(context, loggerFactory, ex, "Failed to retrieve admin API version info.");
        }
    }

    private static IResult HandleGetCapabilities(HttpContext context, ILoggerFactory loggerFactory)
    {
        try
        {
            var response = new AdminCapabilitiesResponse
            {
                MetadataApiVersion = MetadataV2Constants.ApiVersion,
                MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
                ServerVersion = GetServerVersion()
            };
            return Results.Json(
                ApiResponse<AdminCapabilitiesResponse>.CreateSuccess(response),
                AdminInfoJsonContext.Default.ApiResponseAdminCapabilitiesResponse);
        }
        catch (Exception ex)
        {
            return HandleAdminInfoFailure(context, loggerFactory, ex, "Failed to retrieve admin API capabilities.");
        }
    }

    private static IResult HandleAdminInfoFailure(
        HttpContext context,
        ILoggerFactory loggerFactory,
        Exception exception,
        string detail)
    {
        var logger = loggerFactory.CreateLogger(typeof(AdminInfoEndpoints).FullName!);
        AdminInfoEndpointsLog.EndpointFailed(logger, detail, exception);
        return StandardErrorHelpers.CreateInternalServerError(context, detail);
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
}

public sealed record AdminCapabilitiesResponse
{
    [JsonPropertyName("metadataApiVersion")]
    public string MetadataApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("metadataSchemaVersion")]
    public string MetadataSchemaVersion { get; init; } = string.Empty;

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;
}

[JsonSerializable(typeof(ApiResponse<AdminVersionResponse>))]
[JsonSerializable(typeof(ApiResponse<AdminCapabilitiesResponse>))]
internal sealed partial class AdminInfoJsonContext : JsonSerializerContext
{
}

internal static partial class AdminInfoEndpointsLog
{
    [LoggerMessage(EventId = 4570, Level = LogLevel.Error, Message = "Admin info endpoint failed: {Detail}")]
    public static partial void EndpointFailed(ILogger logger, string detail, Exception exception);
}
