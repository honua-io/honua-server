// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.RateLimiting;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static class LocalConsoleSessionEndpoint
{
    internal static void MapLocalConsoleSessionEndpoint(this IEndpointRouteBuilder group) =>
        group.MapPost("/local-console-session", HandleAsync)
            .WithDisplayName("Issue Local Console Human Session")
            .WithMetadata(new RateLimitAttribute(10));

    internal static async Task<IResult> HandleAsync(HttpContext context, IConfiguration configuration,
        IHostEnvironment environment, OperatorBearerTokenService tokens)
    {
        context.Response.Headers.CacheControl = "no-store";
        var section = configuration.GetSection("AgentWorkspace:LocalHumanBridge");
        if (!environment.IsDevelopment() || !section.GetValue<bool>("Enabled"))
        {
            return Results.NotFound();
        }
        var configured = section["Key"];
        var supplied = context.Request.Headers["X-Honua-Local-Bridge"];
        // Never accept an agent/API-key request as a human exchange, even if misconfigured with a shared key.
        if (configured is null || Encoding.UTF8.GetByteCount(configured) < 32 || supplied.Count != 1 ||
            context.Request.Headers.ContainsKey("Authorization") || context.Request.Headers.ContainsKey("X-API-Key") ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(configured)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied.ToString()))))
        {
            return Results.Unauthorized();
        }
        var tenant = section["TenantId"];
        if (string.IsNullOrWhiteSpace(tenant) || tenant.Length > 128 || !tokens.Enabled)
        {
            return Results.StatusCode(503);
        }
        if (context.Request.ContentLength is null or > 1024)
        {
            return Results.BadRequest();
        }
        string? subject;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body,
                new JsonDocumentOptions { MaxDepth = 2 }, context.RequestAborted);
            if (body.RootElement.ValueKind != JsonValueKind.Object ||
                body.RootElement.EnumerateObject().Count() != 1 ||
                !body.RootElement.TryGetProperty("subject", out var value) || value.ValueKind != JsonValueKind.String)
            {
                return Results.BadRequest();
            }
            subject = value.GetString();
        }
        catch (JsonException) { return Results.BadRequest(); }
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 128 ||
            subject.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '@' and not ':'))
        {
            return Results.BadRequest();
        }
        var issued = tokens.Issue([
            new() { Type = ClaimTypes.NameIdentifier, Value = "local-console:" + subject },
            new() { Type = "tenant_id", Value = tenant },
            new() { Type = "roles", Value = "admin" }
        ], DateTimeOffset.UtcNow.AddMinutes(10));
        return issued is null ? Results.StatusCode(503) : Results.Ok(new AdminOperatorBearerResponse
        {
            AccessToken = issued.Token,
            TokenType = "Bearer",
            ExpiresAt = issued.ExpiresAt,
            ExpiresIn = (long)Math.Max(0, (issued.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)
        });
    }
}
