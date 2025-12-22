// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// API Key authentication handler with development bypass mode support
/// </summary>
/// <remarks>
/// Initializes a new instance of the ApiKeyAuthenticationHandler
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration,
    IWebHostEnvironment environment) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string ApiKeyHeader = "X-API-Key";
    private const string DevAuthBypassEnvVar = "HONUA_DEV_AUTH";
    private const string AdminPasswordEnvVar = "HONUA_ADMIN_PASSWORD";

    private readonly IConfiguration _configuration = configuration;
    private readonly IWebHostEnvironment _environment = environment;

    /// <summary>
    /// Handles API key authentication with development bypass support
    /// </summary>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for development bypass modes
        if (IsDevelopmentBypassEnabled())
        {
            AuthenticationLog.DevelopmentBypassEnabled(Logger);
            return Task.FromResult(CreateSuccessfulAuthenticationResult("dev-bypass"));
        }

        // Extract API key from header
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out StringValues apiKeyValues))
        {
            AuthenticationLog.NoApiKeyFound(Logger, ApiKeyHeader);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? providedApiKey = apiKeyValues.FirstOrDefault();
        if (string.IsNullOrEmpty(providedApiKey))
        {
            AuthenticationLog.EmptyApiKeyProvided(Logger, ApiKeyHeader);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Get configured admin password
        string? configuredPassword = _configuration[AdminPasswordEnvVar];
        if (string.IsNullOrEmpty(configuredPassword))
        {
            AuthenticationLog.NoAdminPasswordConfigured(Logger, AdminPasswordEnvVar);
            // Store the failure message for the challenge handler
            Context.Items["AuthFailureMessage"] = "Admin authentication not configured";
            return Task.FromResult(AuthenticateResult.Fail("Admin authentication not configured"));
        }

        // Perform constant-time comparison to prevent timing attacks
        if (!IsApiKeyValid(providedApiKey, configuredPassword))
        {
            AuthenticationLog.InvalidApiKeyProvided(Logger);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        AuthenticationLog.ApiKeyAuthenticationSuccessful(Logger);
        return Task.FromResult(CreateSuccessfulAuthenticationResult("admin"));
    }

    /// <summary>
    /// Determines if development authentication bypass is enabled
    /// </summary>
    private bool IsDevelopmentBypassEnabled()
    {
        // Check if HONUA_DEV_AUTH is explicitly set to true
        string? devAuthBypass = _configuration[DevAuthBypassEnvVar];
        if (string.Equals(devAuthBypass, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if we're in development environment AND admin password is empty/not configured
        if (_environment.IsDevelopment())
        {
            string? adminPassword = _configuration[AdminPasswordEnvVar];
            if (string.IsNullOrEmpty(adminPassword))
            {
                AuthenticationLog.DevelopmentEnvironmentAuthBypass(Logger);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Performs constant-time comparison of API keys to prevent timing attacks
    /// </summary>
    private static bool IsApiKeyValid(string providedKey, string configuredKey)
    {
        if (providedKey.Length != configuredKey.Length)
            return false;

        byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
        byte[] configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        bool areEqual = true;
        for (int i = 0; i < providedBytes.Length; i++)
        {
            areEqual &= providedBytes[i] == configuredBytes[i];
        }

        return areEqual;
    }

    /// <summary>
    /// Creates a successful authentication result with admin claims
    /// </summary>
    private AuthenticateResult CreateSuccessfulAuthenticationResult(string authenticationType)
    {
        Claim[] claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("auth_type", authenticationType)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Handles authentication challenges by returning 401 Unauthorized
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.ContentType = "application/problem+json; charset=utf-8";

        // Check if there's a specific failure message from authentication
        string? failureMessage = Context.Items["AuthFailureMessage"] as string;
        if (!string.IsNullOrEmpty(failureMessage))
        {
            string problemDetails = $$"""{"title":"Unauthorized","status":401,"detail":"{{failureMessage}}"}""";
            return Response.WriteAsync(problemDetails);
        }

        bool devBypassEnabled = IsDevelopmentBypassEnabled();
        string defaultDetails = devBypassEnabled
            ? """{"title":"Unauthorized","status":401,"detail":"API key required. Development bypass is enabled but this request still requires authentication."}"""
            : """{"title":"Unauthorized","status":401,"detail":"API key required. Provide a valid API key in the X-API-Key header."}""";

        return Response.WriteAsync(defaultDetails);
    }
}
