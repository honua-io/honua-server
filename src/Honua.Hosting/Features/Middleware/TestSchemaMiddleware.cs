// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Middleware that applies a test schema header to the request-scoped schema context.
/// </summary>
internal sealed partial class TestSchemaMiddleware
{
    private const string SchemaHeaderName = "X-Honua-Test-Schema";
    private static readonly Regex _schemaNameRegex = SchemaNamePattern();
    private readonly RequestDelegate _next;
    private readonly ILogger<TestSchemaMiddleware> _logger;

    public TestSchemaMiddleware(RequestDelegate next, ILogger<TestSchemaMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, SchemaContext schemaContext)
    {
        var headerValue = context.Request.Headers[SchemaHeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(headerValue))
        {
            var schemaName = headerValue.Trim();
            if (!_schemaNameRegex.IsMatch(schemaName))
            {
                // The header value failed identifier validation, so it may carry control chars
                // (CR/LF) used for log forging. Log only a short hash for correlation across
                // repeat offenders without exposing the raw bytes.
                Log.InvalidSchemaHeader(_logger, LogValueRedactor.Hash(schemaName));
                var errorResponse = new StandardErrorResponse(
                    StatusCodes.Status400BadRequest,
                    "Invalid test schema header.",
                    "The supplied schema header is not a valid identifier.");
                await StandardErrorResponseFormatter
                    .FormatError(context, errorResponse)
                    .ExecuteAsync(context);
                return;
            }

            schemaContext.CurrentSchema = schemaName;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            schemaContext.CurrentSchema = null;
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8701,
            Level = LogLevel.Warning,
            Message = "Invalid test schema header rejected; correlation hash {SchemaHash}.")]
        public static partial void InvalidSchemaHeader(ILogger logger, string schemaHash);
    }
}

internal static class TestSchemaMiddlewareExtensions
{
    public static IApplicationBuilder UseTestSchemaHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TestSchemaMiddleware>();
    }
}
