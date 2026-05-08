// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Server.Features.CloudDemo;

internal static class CloudDemoEndpointGuards
{
    private const string WritableServiceId = "field-inspections-demo-writable";

    private static readonly HashSet<string> _serviceWriteOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "applyEdits",
        "append",
        "createReplica",
        "synchronizeReplica",
        "unRegisterReplica"
    };

    private static readonly HashSet<string> _layerWriteOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "applyEdits",
        "addFeatures",
        "updateFeatures",
        "deleteFeatures",
        "append",
        "calculate"
    };

    private static readonly HashSet<string> _attachmentWriteOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "addAttachment",
        "updateAttachment",
        "deleteAttachments"
    };

    public static IApplicationBuilder UseCloudDemoServiceLayerAliases(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            if (CloudDemoConfiguration.IsEnabled(configuration) &&
                CloudDemoLayerAliases.TryRewriteFeatureServerPath(context.Request.Path, out var rewrittenPath))
            {
                context.Request.Path = rewrittenPath;
            }

            if (CloudDemoConfiguration.IsEnabled(configuration))
            {
                TryRewriteFeatureServerRouteValues(context);
            }

            await next(context).ConfigureAwait(false);
        });
    }

    public static IApplicationBuilder UseCloudDemoWritableFeatureGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            if (!CloudDemoConfiguration.IsEnabled(configuration) ||
                !IsGuardedWritableFeatureServerRequest(context.Request))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (!CloudDemoConfiguration.AllowWrites(configuration))
            {
                await WriteGuardFailureAsync(
                        context,
                        StatusCodes.Status403Forbidden,
                        "cloud_demo_writes_disabled",
                        "Cloud demo writes are disabled.")
                    .ConfigureAwait(false);
                return;
            }

            var writeToken = CloudDemoConfiguration.WriteToken(configuration);
            if (string.IsNullOrWhiteSpace(writeToken))
            {
                await WriteGuardFailureAsync(
                        context,
                        StatusCodes.Status503ServiceUnavailable,
                        "cloud_demo_write_token_not_configured",
                        "Cloud demo write token is not configured.")
                    .ConfigureAwait(false);
                return;
            }

            if (!CloudDemoCredentials.TokenMatches(context.Request, CloudDemoConfiguration.WriteTokenHeader, writeToken))
            {
                var statusCode = CloudDemoCredentials.HasPresentedToken(context.Request, CloudDemoConfiguration.WriteTokenHeader)
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status401Unauthorized;
                await WriteGuardFailureAsync(
                        context,
                        statusCode,
                        "cloud_demo_write_token_invalid",
                        "Cloud demo write token is required.")
                    .ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool IsGuardedWritableFeatureServerRequest(HttpRequest request)
    {
        var value = request.Path.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 ||
            !string.Equals(segments[0], "rest", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "services", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[2], WritableServiceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[3], "FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments.Length == 5)
        {
            return IsServiceWriteOperation(request.Method, segments[4]);
        }

        if (!int.TryParse(segments[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId) ||
            (layerId != 0 && layerId != CloudDemoLayerAliases.FieldInspectionsWritableLayer))
        {
            return false;
        }

        if (segments.Length == 6)
        {
            return IsLayerWriteOperation(request.Method, segments[5]);
        }

        return segments.Length == 7 &&
            long.TryParse(segments[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            IsAttachmentWriteOperation(request.Method, segments[6]);
    }

    private static bool IsServiceWriteOperation(string method, string operation)
        => HttpMethods.IsPost(method) && _serviceWriteOperations.Contains(operation);

    private static bool IsLayerWriteOperation(string method, string operation)
        => _layerWriteOperations.Contains(operation) &&
            (HttpMethods.IsPost(method) ||
                (HttpMethods.IsGet(method) && string.Equals(operation, "calculate", StringComparison.OrdinalIgnoreCase)));

    private static bool IsAttachmentWriteOperation(string method, string operation)
        => HttpMethods.IsPost(method) && _attachmentWriteOperations.Contains(operation);

    private static bool TryRewriteFeatureServerRouteValues(HttpContext context)
    {
        if (!context.Request.RouteValues.TryGetValue("serviceId", out var serviceValue) ||
            !context.Request.RouteValues.TryGetValue("layerId", out var layerValue))
        {
            return false;
        }

        var serviceId = serviceValue?.ToString();
        var layerText = layerValue?.ToString();
        if (string.IsNullOrWhiteSpace(serviceId) ||
            string.IsNullOrWhiteSpace(layerText) ||
            !int.TryParse(layerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contractLayerId) ||
            !CloudDemoLayerAliases.TryResolve(serviceId, contractLayerId, out var physicalLayerId) ||
            physicalLayerId == contractLayerId)
        {
            return false;
        }

        context.Request.RouteValues["layerId"] = physicalLayerId.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static async Task WriteGuardFailureAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        var response = new CloudDemoProblemResponse(code, message);
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                CloudDemoJsonContext.Default.CloudDemoProblemResponse,
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}
