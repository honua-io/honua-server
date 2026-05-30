// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Mobile.Diagnostics;

/// <summary>
/// Mobile runtime diagnostics ingestion endpoints.
/// </summary>
internal static class MobileExceptionIngestionEndpoints
{
    private const string ProtocolTag = "mobile-diagnostics";

    /// <summary>
    /// Maps the mobile exception upload endpoint consumed by honua-mobile.
    /// </summary>
    public static void MapMobileExceptionIngestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        _ = endpoints.MapPost("/api/mobile/exceptions", HandleUploadException)
            .WithName("UploadMobileExceptionReport")
            .WithSummary("Upload a sanitized mobile exception report")
            .WithTags("Mobile", "Diagnostics")
            .RequireAdminAuthorization();
    }

    private static IResult HandleUploadException(
        HttpContext context,
        [FromBody] JsonElement report,
        [FromServices] ILoggerFactory loggerFactory)
    {
        if (report.ValueKind != JsonValueKind.Object)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Mobile exception report must be a JSON object.");
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("MobileDiagnostics.ExceptionIngest", ActivityKind.Server);
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "exception-ingest");

        var reportId = ReadString(report, "id");
        var source = ReadString(report, "source") ?? "unknown";
        var exceptionType = ReadString(report, "exceptionType", "exception_type") ?? "unknown";
        var severity = ReadString(report, "severity") ?? "unknown";

        activity?.SetTag("honua.mobile_exception.source", source);
        activity?.SetTag("honua.mobile_exception.exception_type", exceptionType);
        activity?.SetTag("honua.mobile_exception.severity", severity);

        var logger = loggerFactory.CreateLogger(typeof(MobileExceptionIngestionEndpoints));
        MobileExceptionIngestionLog.ReportAccepted(
            logger,
            string.IsNullOrWhiteSpace(reportId) ? "missing" : reportId,
            source,
            exceptionType,
            severity);

        ApplyNoStoreHeaders(context.Response);
        return Results.Json(
            new MobileExceptionIngestionResponse { Accepted = true },
            MobileExceptionIngestionJsonContext.Default.MobileExceptionIngestionResponse);
    }

    private static string? ReadString(JsonElement report, params string[] names)
    {
        foreach (var name in names)
        {
            if (report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static void ApplyNoStoreHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}

internal sealed class MobileExceptionIngestionResponse
{
    public required bool Accepted { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(MobileExceptionIngestionResponse))]
internal sealed partial class MobileExceptionIngestionJsonContext : JsonSerializerContext
{
}

internal static partial class MobileExceptionIngestionLog
{
    [LoggerMessage(
        EventId = 9243,
        Level = LogLevel.Information,
        Message = "Mobile exception report accepted. ReportId={ReportId} Source={Source} ExceptionType={ExceptionType} Severity={Severity}")]
    public static partial void ReportAccepted(
        ILogger logger,
        string reportId,
        string source,
        string exceptionType,
        string severity);
}
