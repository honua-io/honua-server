// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Infrastructure.Security;

public static class CspViolationReportEndpoint
{
    private const int MaxViolationReportPayloadBytes = 64 * 1024;
    private const int MaxLogFieldLength = 512;
    private const int MaxUserAgentLogLength = 256;

    public static void MapCspViolationReportEndpoint(this WebApplication app)
    {
        app.MapPost("/csp-violation-report", HandleCspViolationReport)
            .WithName("CspViolationReport")
            .WithSummary("Handle CSP violation reports")
            .WithDescription("Receives and logs Content Security Policy violation reports from browsers")
            .WithTags("Security")
            .ExcludeFromDescription() // Don't include in OpenAPI docs for security
            .AllowAnonymous(); // CSP reports come from browsers without authentication
    }

    private static async Task<IResult> HandleCspViolationReport(
        HttpRequest request,
        [FromServices] ILogger<CspViolationReport> logger,
        HttpContext context)
    {
        var clientInfo = GetClientInfo(context);

        try
        {
            if (request.ContentLength == 0)
            {
                CspViolationReportLog.EmptyViolationReport(logger, clientInfo);
                return Results.NoContent();
            }

            var payload = await ReadPayloadWithLimitAsync(request, logger, clientInfo, context.RequestAborted);
            if (payload == null)
            {
                return Results.NoContent();
            }

            if (payload.Length == 0)
            {
                CspViolationReportLog.EmptyViolationReport(logger, clientInfo);
                return Results.NoContent();
            }

            var violationReport = JsonSerializer.Deserialize(
                payload,
                CspViolationJsonContext.Default.CspViolationReport);

            if (violationReport?.CspReport != null)
            {
                var violation = violationReport.CspReport;

                // Log the violation with structured data
                CspViolationReportLog.CspViolationReported(
                    logger,
                    SanitizeForLog(violation.BlockedUri, MaxLogFieldLength),
                    SanitizeForLog(violation.ViolatedDirective, MaxLogFieldLength),
                    SanitizeForLog(violation.OriginalPolicy, MaxLogFieldLength),
                    SanitizeForLog(violation.DocumentUri, MaxLogFieldLength),
                    clientInfo.IpAddress,
                    clientInfo.UserAgent);

                // In production environments, you might want to:
                // 1. Store violations in a database for analysis
                // 2. Send alerts for critical violations
                // 3. Aggregate violations for policy tuning
                // 4. Rate limit violation reports to prevent abuse

                // Example: Check for potentially malicious patterns
                if (IsSuspiciousViolation(violation))
                {
                    CspViolationReportLog.SuspiciousCspViolation(
                        logger,
                        SanitizeForLog(violation.BlockedUri, MaxLogFieldLength),
                        SanitizeForLog(violation.SourceFile, MaxLogFieldLength),
                        clientInfo.IpAddress,
                        clientInfo.UserAgent);
                }
            }
            else if (violationReport == null)
            {
                CspViolationReportLog.EmptyViolationReport(logger, clientInfo);
            }
            else
            {
                CspViolationReportLog.InvalidViolationReport(logger, "<unavailable>", clientInfo);
            }
        }
        catch (JsonException ex)
        {
            CspViolationReportLog.ViolationReportParsingError(logger, ex.Message, clientInfo);
        }
        // Intentional broad catch: this is a best-effort CSP violation report ingestion endpoint (after
        // the more specific JsonException case above); any failure while processing the browser-supplied
        // report is logged, and the endpoint still returns 204 below to acknowledge receipt.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CspViolationReportLog.ViolationReportProcessingError(logger, ex.Message, clientInfo);
        }

        // Always return 204 No Content to acknowledge receipt
        // Don't provide details about processing to avoid information leakage
        return Results.NoContent();
    }

    private static bool IsSuspiciousViolation(CspViolationDetails violation)
    {
        var blockedUri = violation.BlockedUri?.ToLowerInvariant();
        var sourceFile = violation.SourceFile?.ToLowerInvariant();

        // Check for common malicious patterns
        var suspiciousPatterns = new[]
        {
            "javascript:",
            "data:text/html",
            "vbscript:",
            "chrome-extension:",
            "moz-extension:",
            "safari-extension:",
            "about:blank",
            "eval(",
            "javascript:void(0)"
        };

        return suspiciousPatterns.Any(pattern =>
            blockedUri?.Contains(pattern) == true ||
            sourceFile?.Contains(pattern) == true);
    }

    private static (string IpAddress, string UserAgent) GetClientInfo(HttpContext context)
    {
        var ipAddress = SanitizeForLog(context.Connection.RemoteIpAddress?.ToString(), maxLength: 128);
        var userAgent = SanitizeForLog(context.Request.Headers.UserAgent.ToString(), MaxUserAgentLogLength);

        return (ipAddress, userAgent);
    }

    private static async Task<byte[]?> ReadPayloadWithLimitAsync(
        HttpRequest request,
        ILogger logger,
        (string IpAddress, string UserAgent) clientInfo,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxViolationReportPayloadBytes)
        {
            CspViolationReportLog.ViolationReportTooLarge(
                logger,
                request.ContentLength.Value,
                MaxViolationReportPayloadBytes,
                clientInfo);
            return null;
        }

        var rentedBuffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new MemoryStream(
                request.ContentLength is > 0 and <= MaxViolationReportPayloadBytes
                    ? (int)request.ContentLength.Value
                    : 0);

            long totalRead = 0;
            while (true)
            {
                var read = await request.Body.ReadAsync(
                    rentedBuffer.AsMemory(0, rentedBuffer.Length),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > MaxViolationReportPayloadBytes)
                {
                    CspViolationReportLog.ViolationReportTooLarge(
                        logger,
                        totalRead,
                        MaxViolationReportPayloadBytes,
                        clientInfo);
                    return null;
                }

                await stream.WriteAsync(rentedBuffer.AsMemory(0, read), cancellationToken);
            }

            return stream.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static string SanitizeForLog(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(capacity: Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                continue;
            }

            builder.Append(c);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}

public sealed class CspViolationReport
{
    public CspViolationDetails? CspReport { get; set; }
}

public sealed class CspViolationDetails
{
    public string? BlockedUri { get; set; }

    public string? ViolatedDirective { get; set; }

    public string? OriginalPolicy { get; set; }

    public string? DocumentUri { get; set; }

    public string? SourceFile { get; set; }

    public int? LineNumber { get; set; }

    public int? ColumnNumber { get; set; }

    public string? EffectiveDirective { get; set; }

    public string? Sample { get; set; }

    public int? StatusCode { get; set; }
}

internal static partial class CspViolationReportLog
{
    [LoggerMessage(
        EventId = 4300,
        Level = LogLevel.Warning,
        Message = "CSP violation reported - Blocked URI: {BlockedUri}, Directive: {ViolatedDirective}, Policy: {OriginalPolicy}, Document: {DocumentUri}, Client IP: {ClientIp}, User Agent: {UserAgent}")]
    public static partial void CspViolationReported(
        ILogger logger,
        string blockedUri,
        string violatedDirective,
        string originalPolicy,
        string documentUri,
        string clientIp,
        string userAgent);

    [LoggerMessage(
        EventId = 4301,
        Level = LogLevel.Error,
        Message = "Suspicious CSP violation detected - Blocked URI: {BlockedUri}, Source: {SourceFile}, Client IP: {ClientIp}, User Agent: {UserAgent}")]
    public static partial void SuspiciousCspViolation(
        ILogger logger,
        string blockedUri,
        string sourceFile,
        string clientIp,
        string userAgent);

    [LoggerMessage(
        EventId = 4302,
        Level = LogLevel.Information,
        Message = "Empty CSP violation report received from {ClientInfo}")]
    public static partial void EmptyViolationReport(ILogger logger, (string IpAddress, string UserAgent) clientInfo);

    [LoggerMessage(
        EventId = 4303,
        Level = LogLevel.Warning,
        Message = "Invalid CSP violation report format - Content: {JsonContent}, Client: {ClientInfo}")]
    public static partial void InvalidViolationReport(ILogger logger, string jsonContent, (string IpAddress, string UserAgent) clientInfo);

    [LoggerMessage(
        EventId = 4304,
        Level = LogLevel.Error,
        Message = "Failed to parse CSP violation report JSON - Error: {ErrorMessage}, Client: {ClientInfo}")]
    public static partial void ViolationReportParsingError(ILogger logger, string errorMessage, (string IpAddress, string UserAgent) clientInfo);

    [LoggerMessage(
        EventId = 4305,
        Level = LogLevel.Error,
        Message = "Error processing CSP violation report - Error: {ErrorMessage}, Client: {ClientInfo}")]
    public static partial void ViolationReportProcessingError(ILogger logger, string errorMessage, (string IpAddress, string UserAgent) clientInfo);

    [LoggerMessage(
        EventId = 4306,
        Level = LogLevel.Warning,
        Message = "CSP violation report payload exceeded maximum size - Size: {PayloadBytes}, Max: {MaxBytes}, Client: {ClientInfo}")]
    public static partial void ViolationReportTooLarge(
        ILogger logger,
        long payloadBytes,
        int maxBytes,
        (string IpAddress, string UserAgent) clientInfo);
}
