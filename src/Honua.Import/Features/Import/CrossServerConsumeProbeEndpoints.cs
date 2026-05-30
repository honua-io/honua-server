// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Import;

/// <summary>
/// Test-only endpoints used by the cross-server consume suite to route outbound
/// reference-server reads through Honua's HTTP pipeline.
/// </summary>
internal static partial class CrossServerConsumeProbeEndpoints
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maps cross-server consume probe endpoints when the host runs in the Test environment.
    /// </summary>
    public static IEndpointRouteBuilder MapCrossServerConsumeProbeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.MapGet("/__test/cross-server-consume/proxy", HandleProxyAsync)
            .WithName("CrossServerConsumeProxy")
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task HandleProxyAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Honua.Server.CrossServerConsumeProbe");
        var cancellationToken = context.RequestAborted;
        var sourceUrl = context.Request.Query["url"].ToString();

        if (!TryValidateSourceUrl(sourceUrl, out var uri, out var validationError))
        {
            CrossServerConsumeProbeLog.InvalidSourceUrl(logger, validationError);
            await WriteTextAsync(
                context,
                StatusCodes.Status400BadRequest,
                validationError,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            var mediaType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = mediaType;
            await context.Response.Body.WriteAsync(body, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CrossServerConsumeProbeLog.SourceRequestTimedOut(logger);
            await WriteTextAsync(
                context,
                StatusCodes.Status504GatewayTimeout,
                "Reference server request timed out.",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            CrossServerConsumeProbeLog.SourceRequestFailed(logger, ex);
            await WriteTextAsync(
                context,
                StatusCodes.Status502BadGateway,
                "Reference server request failed.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryValidateSourceUrl(string? sourceUrl, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceUrl) ||
            !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsedUri) ||
            parsedUri is null)
        {
            error = "A valid absolute source URL is required.";
            return false;
        }

        uri = parsedUri;

        if (uri.Scheme is not ("http" or "https"))
        {
            error = "Source URL must use HTTP or HTTPS.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            error = "Source URL must not include embedded credentials.";
            return false;
        }

        if (!uri.IsLoopback)
        {
            error = "Cross-server consume test probes only allow loopback reference-server URLs.";
            return false;
        }

        return true;
    }

    private static async Task WriteTextAsync(
        HttpContext context,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static partial class CrossServerConsumeProbeLog
    {
        [LoggerMessage(7976, LogLevel.Warning, "Invalid cross-server consume probe source URL: {Reason}")]
        public static partial void InvalidSourceUrl(ILogger logger, string reason);

        [LoggerMessage(7977, LogLevel.Warning, "Cross-server consume probe source request timed out")]
        public static partial void SourceRequestTimedOut(ILogger logger);

        [LoggerMessage(7978, LogLevel.Warning, "Cross-server consume probe source request failed")]
        public static partial void SourceRequestFailed(ILogger logger, Exception exception);
    }
}
