// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;

namespace Honua.Server.Features.Import;

/// <summary>
/// Test-only endpoints used by the cross-server consume suite to route outbound
/// reference-server reads through Honua's HTTP pipeline.
/// </summary>
internal static partial class CrossServerConsumeProbeEndpoints
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);
    private const string ArcGisLicensedConsumeEnv = "HONUA_TEST_ARCGIS_SERVER_CONSUME";
    private const string ArcGisAuthorizationEnv = "HONUA_TEST_ARCGIS_AUTHORIZATION";
    private const string ArcGisTokenEnv = "HONUA_TEST_ARCGIS_TOKEN";

    private static readonly string[] ArcGisSourceUrlEnvironmentVariables =
    [
        "HONUA_TEST_ARCGIS_WMS_URL",
        "HONUA_TEST_ARCGIS_WFS_URL",
        "HONUA_TEST_ARCGIS_WMTS_URL",
        "HONUA_TEST_ARCGIS_MAPSERVER_TILE_URL",
    ];

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
            if (!TryCreateSourceRequest(uri, out var request, out var credentialError))
            {
                CrossServerConsumeProbeLog.InvalidSourceUrl(logger, credentialError);
                await WriteTextAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    credentialError,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            using (request)
            {
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
            if (!IsConfiguredArcGisSourceUri(uri))
            {
                error = "Cross-server consume test probes only allow loopback reference-server URLs unless a licensed ArcGIS Server source is explicitly configured.";
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateSourceRequest(Uri sourceUri, out HttpRequestMessage request, out string error)
    {
        error = string.Empty;
        request = null!;

        var isArcGisSource = IsConfiguredArcGisSourceUri(sourceUri);
        var requestUri = isArcGisSource
            ? AddArcGisTokenIfConfigured(sourceUri)
            : sourceUri;

        request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        if (!isArcGisSource)
        {
            return true;
        }

        var authorization = Environment.GetEnvironmentVariable(ArcGisAuthorizationEnv);
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return true;
        }

        if (AuthenticationHeaderValue.TryParse(authorization, out var headerValue))
        {
            request.Headers.Authorization = headerValue;
            return true;
        }

        request.Dispose();
        request = null!;
        error = "Configured ArcGIS authorization header is invalid.";
        return false;
    }

    private static Uri AddArcGisTokenIfConfigured(Uri sourceUri)
    {
        var token = Environment.GetEnvironmentVariable(ArcGisTokenEnv);
        if (string.IsNullOrWhiteSpace(token) ||
            sourceUri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase))
        {
            return sourceUri;
        }

        var builder = new UriBuilder(sourceUri);
        var existingQuery = builder.Query.TrimStart('?');
        var tokenQuery = $"token={Uri.EscapeDataString(token)}";
        builder.Query = string.IsNullOrWhiteSpace(existingQuery)
            ? tokenQuery
            : $"{existingQuery}&{tokenQuery}";
        return builder.Uri;
    }

    private static bool IsConfiguredArcGisSourceUri(Uri sourceUri)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ArcGisLicensedConsumeEnv)))
        {
            return false;
        }

        foreach (var variable in ArcGisSourceUrlEnvironmentVariables)
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(configured) ||
                !Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) ||
                configuredUri is null ||
                !string.IsNullOrWhiteSpace(configuredUri.UserInfo))
            {
                continue;
            }

            if (UriAuthorityMatches(configuredUri, sourceUri) &&
                UriPathIsAtOrBelow(configuredUri.AbsolutePath, sourceUri.AbsolutePath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UriAuthorityMatches(Uri configuredUri, Uri sourceUri)
        => string.Equals(configuredUri.Scheme, sourceUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(configuredUri.Host, sourceUri.Host, StringComparison.OrdinalIgnoreCase) &&
           configuredUri.Port == sourceUri.Port;

    private static bool UriPathIsAtOrBelow(string configuredPath, string sourcePath)
    {
        var normalizedConfiguredPath = configuredPath.TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedConfiguredPath))
        {
            normalizedConfiguredPath = "/";
        }

        return string.Equals(sourcePath, normalizedConfiguredPath, StringComparison.OrdinalIgnoreCase) ||
               sourcePath.StartsWith(normalizedConfiguredPath + "/", StringComparison.OrdinalIgnoreCase);
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
