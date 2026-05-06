// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Mobile.Auth;

/// <summary>
/// Mobile runtime auth refresh endpoints consumed by honua-mobile live image tests.
/// </summary>
internal static class MobileAuthEndpoints
{
    private const string DevelopmentRefreshToken = "refresh-token";
    private const string DevelopmentAccessToken = "live-image-test-bearer-token";
    private const int DefaultExpiresInSeconds = 3600;

    /// <summary>
    /// Maps mobile runtime auth refresh endpoints.
    /// </summary>
    public static void MapMobileAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        _ = endpoints.MapPost("/oauth/token", HandleRefreshToken)
            .WithName("RefreshMobileAuthToken")
            .WithSummary("Refresh a mobile runtime auth token")
            .WithTags("Mobile", "Auth")
            .AllowAnonymous();
    }

    private static async Task<IResult> HandleRefreshToken(
        HttpContext context,
        [FromServices] IOptions<MobileAuthOptions> options,
        [FromServices] IHostEnvironment environment,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(MobileAuthEndpoints));
        var request = await ReadRefreshRequestAsync(context).ConfigureAwait(false);
        if (request is null)
        {
            MobileAuthLog.RefreshRejected(logger, "invalid_request");
            return StandardErrorHelpers.CreateBadRequest(context, "Refresh token request body is required.");
        }

        var grantType = request.EffectiveGrantType?.Trim();
        if (!string.IsNullOrWhiteSpace(grantType) &&
            !string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
        {
            MobileAuthLog.RefreshRejected(logger, "invalid_grant_type");
            return StandardErrorHelpers.CreateBadRequest(context, "Grant type is invalid.");
        }

        var providedRefreshToken = request.EffectiveRefreshToken?.Trim();
        if (string.IsNullOrWhiteSpace(providedRefreshToken))
        {
            MobileAuthLog.RefreshRejected(logger, "missing_refresh_token");
            return StandardErrorHelpers.CreateBadRequest(context, "Refresh token is required.");
        }

        var material = ResolveTokenMaterial(options.Value, environment);
        if (material is null)
        {
            MobileAuthLog.RefreshNotConfigured(logger);
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Mobile auth refresh is not configured.");
        }

        if (!SecretEquals(providedRefreshToken, material.RefreshToken))
        {
            MobileAuthLog.RefreshRejected(logger, "invalid_refresh_token");
            return StandardErrorHelpers.CreateUnauthorized(context, "Refresh token is invalid.");
        }

        ApplyNoStoreHeaders(context.Response);
        MobileAuthLog.RefreshSucceeded(logger);

        return Results.Json(
            new MobileTokenRefreshResponse
            {
                AccessToken = material.AccessToken,
                RefreshToken = material.NextRefreshToken,
                TokenType = "Bearer",
                ExpiresIn = material.ExpiresInSeconds,
            },
            MobileAuthJsonContext.Default.MobileTokenRefreshResponse);
    }

    private static async Task<MobileTokenRefreshRequest?> ReadRefreshRequestAsync(HttpContext context)
    {
        if (context.Request.ContentLength == 0)
        {
            return null;
        }

        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            return new MobileTokenRefreshRequest
            {
                RefreshToken = FirstValue(form["refreshToken"], form["refresh_token"]),
                GrantType = FirstValue(form["grantType"], form["grant_type"]),
            };
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    MobileAuthJsonContext.Default.MobileTokenRefreshRequest,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MobileTokenMaterial? ResolveTokenMaterial(MobileAuthOptions options, IHostEnvironment environment)
    {
        var refreshToken = NullIfWhiteSpace(options.RefreshToken);
        var accessToken = NullIfWhiteSpace(options.AccessToken);
        var nextRefreshToken = NullIfWhiteSpace(options.NextRefreshToken) ?? refreshToken;
        var expiresInSeconds = options.ExpiresInSeconds > 0
            ? options.ExpiresInSeconds
            : DefaultExpiresInSeconds;

        if (refreshToken is null || accessToken is null)
        {
            if (!options.EnableDevelopmentTokenFallback || !IsDevelopmentLike(environment))
            {
                return null;
            }

            refreshToken = DevelopmentRefreshToken;
            accessToken = DevelopmentAccessToken;
            nextRefreshToken = DevelopmentRefreshToken;
            expiresInSeconds = DefaultExpiresInSeconds;
        }

        return new MobileTokenMaterial(
            refreshToken,
            accessToken,
            nextRefreshToken ?? refreshToken,
            expiresInSeconds);
    }

    private static bool IsDevelopmentLike(IHostEnvironment environment)
        => environment.IsDevelopment() ||
           environment.IsEnvironment("Test") ||
           environment.IsEnvironment("IntegrationTest");

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstValue(params Microsoft.Extensions.Primitives.StringValues[] values)
    {
        foreach (var value in values)
        {
            var candidate = value.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool SecretEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static void ApplyNoStoreHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private sealed record MobileTokenMaterial(
        string RefreshToken,
        string AccessToken,
        string NextRefreshToken,
        int ExpiresInSeconds);
}

internal sealed class MobileAuthOptions
{
    public const string SectionName = "Mobile:Auth";

    public string? RefreshToken { get; set; }

    public string? AccessToken { get; set; }

    public string? NextRefreshToken { get; set; }

    public int ExpiresInSeconds { get; set; } = 3600;

    public bool EnableDevelopmentTokenFallback { get; set; } = true;
}

internal sealed class MobileTokenRefreshRequest
{
    public string? RefreshToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshTokenSnakeCase { get; init; }

    public string? GrantType { get; init; }

    [JsonPropertyName("grant_type")]
    public string? GrantTypeSnakeCase { get; init; }

    [JsonIgnore]
    public string? EffectiveRefreshToken => RefreshToken ?? RefreshTokenSnakeCase;

    [JsonIgnore]
    public string? EffectiveGrantType => GrantType ?? GrantTypeSnakeCase;
}

internal sealed class MobileTokenRefreshResponse
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required string TokenType { get; init; }

    public required int ExpiresIn { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MobileTokenRefreshRequest))]
[JsonSerializable(typeof(MobileTokenRefreshResponse))]
internal sealed partial class MobileAuthJsonContext : JsonSerializerContext
{
}

internal static partial class MobileAuthLog
{
    [LoggerMessage(
        EventId = 9240,
        Level = LogLevel.Information,
        Message = "Mobile auth token refresh succeeded.")]
    public static partial void RefreshSucceeded(ILogger logger);

    [LoggerMessage(
        EventId = 9241,
        Level = LogLevel.Warning,
        Message = "Mobile auth token refresh rejected. Reason={Reason}")]
    public static partial void RefreshRejected(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 9242,
        Level = LogLevel.Warning,
        Message = "Mobile auth token refresh is not configured.")]
    public static partial void RefreshNotConfigured(ILogger logger);
}
