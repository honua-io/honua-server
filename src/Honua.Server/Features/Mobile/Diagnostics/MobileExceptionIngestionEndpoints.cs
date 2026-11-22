// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Mobile.Diagnostics;

internal static class MobileExceptionIngestionEndpoints
{
    private const string ProtocolTag = "mobile-diagnostics";

    private static readonly DiagnosticFieldRule ReportIdRule = new()
    {
        DisplayName = "id",
        Fallback = "missing",
        MaxLength = 128,
        IsAllowedChar = IsValidIdentifierChar,
        PropertyNames = ["id"],
    };

    private static readonly DiagnosticFieldRule SourceRule = new()
    {
        DisplayName = "source",
        Fallback = "unknown",
        MaxLength = 128,
        IsAllowedChar = IsValidSourceChar,
        PropertyNames = ["source"],
    };

    private static readonly DiagnosticFieldRule ExceptionTypeRule = new()
    {
        DisplayName = "exceptionType",
        Fallback = "unknown",
        MaxLength = 512,
        IsAllowedChar = IsValidExceptionTypeChar,
        PropertyNames = ["exceptionType", "exception_type"],
    };

    private static readonly DiagnosticFieldRule SeverityRule = new()
    {
        DisplayName = "severity",
        Fallback = "unknown",
        MaxLength = 32,
        IsAllowedChar = IsValidSeverityChar,
        PropertyNames = ["severity"],
    };

    public static void MapMobileExceptionIngestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        _ = endpoints.MapPost("/api/mobile/exceptions", HandleUploadException)
            .WithName("UploadMobileExceptionReport")
            .WithSummary("Upload a sanitized mobile exception report")
            .WithTags("Mobile", "Diagnostics")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
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

        var fields = ReadDiagnosticFields(report);
        if (fields.Error is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, fields.Error);
        }

        RecordAcceptedReport(fields, loggerFactory);

        ApplyNoStoreHeaders(context.Response);
        return Results.Json(
            new MobileExceptionIngestionResponse { Accepted = true },
            MobileExceptionIngestionJsonContext.Default.MobileExceptionIngestionResponse);
    }

    private static ValidatedDiagnosticFields ReadDiagnosticFields(JsonElement report)
    {
        var reportId = ReadValidatedField(report, ReportIdRule);
        var source = ReadValidatedField(report, SourceRule);
        var exceptionType = ReadValidatedField(report, ExceptionTypeRule);
        var severity = ReadValidatedField(report, SeverityRule);

        var error = reportId.Error ?? source.Error ?? exceptionType.Error ?? severity.Error;
        return new ValidatedDiagnosticFields
        {
            ReportId = reportId.Value ?? string.Empty,
            Source = source.Value ?? string.Empty,
            ExceptionType = exceptionType.Value ?? string.Empty,
            Severity = severity.Value ?? string.Empty,
            Error = error,
        };
    }

    private static void RecordAcceptedReport(in ValidatedDiagnosticFields fields, ILoggerFactory loggerFactory)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("MobileDiagnostics.ExceptionIngest", ActivityKind.Server);
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "exception-ingest");
        activity?.SetTag("honua.mobile_exception.source", fields.Source);
        activity?.SetTag("honua.mobile_exception.exception_type", fields.ExceptionType);
        activity?.SetTag("honua.mobile_exception.severity", fields.Severity);

        var logger = loggerFactory.CreateLogger(typeof(MobileExceptionIngestionEndpoints));
        MobileExceptionIngestionLog.ReportAccepted(
            logger,
            fields.ReportId,
            fields.Source,
            fields.ExceptionType,
            fields.Severity);
    }

    private static DiagnosticFieldResult ReadValidatedField(JsonElement report, in DiagnosticFieldRule rule)
    {
        var raw = ReadString(report, rule.PropertyNames);
        if (raw is null)
        {
            return DiagnosticFieldResult.Valid(rule.Fallback);
        }

        if (ContainsDisallowedControlCharacter(raw))
        {
            return DiagnosticFieldResult.Invalid($"Field '{rule.DisplayName}' must not contain control characters.");
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return DiagnosticFieldResult.Valid(rule.Fallback);
        }

        if (trimmed.Length > rule.MaxLength)
        {
            return DiagnosticFieldResult.Invalid($"Field '{rule.DisplayName}' must be at most {rule.MaxLength} characters.");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal) || !AllCharactersAllowed(trimmed, rule.IsAllowedChar))
        {
            return DiagnosticFieldResult.Invalid($"Field '{rule.DisplayName}' contains unsupported characters.");
        }

        return DiagnosticFieldResult.Valid(trimmed);
    }

    private static bool ContainsDisallowedControlCharacter(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029')
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllCharactersAllowed(string value, Func<char, bool> isAllowedChar)
    {
        foreach (var ch in value)
        {
            if (!isAllowedChar(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidIdentifierChar(char ch)
        => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':' or '@' or '/';

    private static bool IsValidSourceChar(char ch)
        => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':' or '/' or ' ';

    private static bool IsValidExceptionTypeChar(char ch)
        => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '+' or '`' or '[' or ']' or ',' or '=' or ' ';

    private static bool IsValidSeverityChar(char ch)
        => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-';

    private readonly record struct DiagnosticFieldRule
    {
        public required string DisplayName { get; init; }

        public required string Fallback { get; init; }

        public required int MaxLength { get; init; }

        public required Func<char, bool> IsAllowedChar { get; init; }

        public required string[] PropertyNames { get; init; }
    }

    private readonly record struct ValidatedDiagnosticFields
    {
        public required string ReportId { get; init; }

        public required string Source { get; init; }

        public required string ExceptionType { get; init; }

        public required string Severity { get; init; }

        public string? Error { get; init; }
    }

    private readonly record struct DiagnosticFieldResult(string? Value, string? Error)
    {
        public static DiagnosticFieldResult Valid(string value) => new(value, null);

        public static DiagnosticFieldResult Invalid(string error) => new(null, error);
    }

    private static string? ReadString(JsonElement report, params string[] names)
    {
        var nameIndex = 0;
        while (nameIndex < names.Length)
        {
            if (report.TryGetProperty(names[nameIndex], out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { } resolved)
            {
                return resolved;
            }

            nameIndex++;
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
