// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Refuses data-plane writes while a degraded start is still waiting on the database
/// (honua-server#6008). Readiness already answers 503, but FeatureServer applyEdits and
/// other mutations would still run. GET, HEAD, <c>/healthz</c>, and read-only POSTs
/// (GeocodeServer and known WFS reads, honua-server#6044) stay available. This fence
/// does not migrate.
/// </summary>
internal sealed class DegradedStartupMutationFenceMiddleware(RequestDelegate next)
{
    private const string UnreadableOperation = "\u0000";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var degraded = context.RequestServices.GetService<DegradedStartupContext>();
        if (degraded is { IsAwaitingDatabase: true }
            && IsMutating(context.Request.Method)
            && !IsHealthProbe(context.Request.Path)
            && !await IsReadOnlyPostAsync(context).ConfigureAwait(false))
        {
            var reasonCode = degraded.ReasonCode ?? ReadinessReasonCodes.DatabaseStartupDegraded;
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers[ReadinessReasonCodes.HeaderName] = reasonCode;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool IsMutating(string method)
        => HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);

    private static bool IsHealthProbe(PathString path)
        => path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsReadOnlyPostAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        if (IsGeocodeServer(context.Request.Path))
        {
            return true;
        }

        if (!ProtocolRequestClassifier.IsWfs(context.Request.Path))
        {
            return false;
        }

        return await IsWfsReadAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// A path segment equal to GeocodeServer. Every published GeocodeServer POST
    /// (metadata, find, reverse, suggest, batch) is a read.
    /// </summary>
    private static bool IsGeocodeServer(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is null)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Equals("GeocodeServer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Allow only a known WFS read. XML or form body wins over the query when both
    /// name an operation, and either side naming something other than a known read
    /// fails closed. The body is rewound so the dispatcher can parse it again.
    /// </summary>
    private static async Task<bool> IsWfsReadAsync(HttpContext context)
    {
        var queryOperation = QueryOperation(context.Request);
        if (queryOperation is not null && !IsWfsReadOperation(queryOperation))
        {
            return false;
        }

        var bodyOperation = await ReadBodyOperationAsync(context.Request).ConfigureAwait(false);
        if (bodyOperation is not null)
        {
            return IsWfsReadOperation(bodyOperation);
        }

        return queryOperation is not null && IsWfsReadOperation(queryOperation);
    }

    private static bool IsWfsReadOperation(string operation)
        => operation.Equals("GetCapabilities", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("DescribeFeatureType", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("GetFeature", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("GetPropertyValue", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("ListStoredQueries", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("DescribeStoredQueries", StringComparison.OrdinalIgnoreCase);

    private static string? QueryOperation(HttpRequest request)
    {
        if (!request.Query.TryGetValue("request", out var values) || values.Count == 0)
        {
            return null;
        }

        string? read = null;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (!IsWfsReadOperation(trimmed))
            {
                return trimmed;
            }

            read ??= trimmed;
        }

        return read;
    }

    private static async Task<string?> ReadBodyOperationAsync(HttpRequest request)
    {
        if (request.ContentLength == 0)
        {
            return null;
        }

        request.EnableBuffering();
        if (!request.Body.CanSeek)
        {
            return UnreadableOperation;
        }

        var buffer = new byte[4096];
        try
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), request.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return LooksLikeXml(request.ContentType, text)
                ? ReadXmlRootLocalName(text)
                : ReadFormRequestParameter(text);
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static bool LooksLikeXml(string? contentType, string text)
    {
        if (contentType is not null && contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            return ch == '<';
        }

        return false;
    }

    private static string ReadXmlRootLocalName(string text)
    {
        var i = 0;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '\uFEFF'))
        {
            i++;
        }

        while (i < text.Length && text[i] == '<')
        {
            if (i + 1 < text.Length && text[i + 1] == '?')
            {
                var end = text.IndexOf("?>", i, StringComparison.Ordinal);
                if (end < 0)
                {
                    return UnreadableOperation;
                }

                i = end + 2;
            }
            else if (i + 3 < text.Length && text[i + 1] == '!' && text[i + 2] == '-' && text[i + 3] == '-')
            {
                var end = text.IndexOf("-->", i, StringComparison.Ordinal);
                if (end < 0)
                {
                    return UnreadableOperation;
                }

                i = end + 3;
            }
            else if (i + 1 < text.Length && text[i + 1] == '!')
            {
                return UnreadableOperation;
            }
            else
            {
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                var start = i;
                while (i < text.Length && text[i] is not (' ' or '\t' or '\r' or '\n' or '/' or '>'))
                {
                    i++;
                }

                if (start == i)
                {
                    return UnreadableOperation;
                }

                var name = text[start..i];
                var colon = name.LastIndexOf(':');
                return colon >= 0 ? name[(colon + 1)..] : name;
            }

            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }
        }

        return UnreadableOperation;
    }

    private static string? ReadFormRequestParameter(string text)
    {
        string? read = null;
        foreach (var pair in text.Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(pair[..separator].Replace('+', ' '));
                value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' ')).Trim();
            }
            catch (UriFormatException)
            {
                return UnreadableOperation;
            }

            if (!key.Equals("request", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
            {
                continue;
            }

            if (!IsWfsReadOperation(value))
            {
                return value;
            }

            read ??= value;
        }

        return read;
    }
}

/// <summary>Extension methods for <see cref="DegradedStartupMutationFenceMiddleware"/>.</summary>
internal static class DegradedStartupMutationFenceExtensions
{
    /// <summary>
    /// Registers the degraded-start write fence. Place it as soon as the client HTTP method
    /// is visible so a mutation never reaches a handler that writes.
    /// </summary>
    public static IApplicationBuilder UseDegradedStartupMutationFence(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<DegradedStartupMutationFenceMiddleware>();
    }
}
