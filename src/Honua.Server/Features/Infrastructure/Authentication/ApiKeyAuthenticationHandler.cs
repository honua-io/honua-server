// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// API Key authentication handler with development bypass mode support
/// </summary>
/// <remarks>
/// Initializes a new instance of the ApiKeyAuthenticationHandler
/// </remarks>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyAuthenticationDependencies dependencies) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string ApiKeyHeader = "X-API-Key";
    private const string AuthorizationHeader = "Authorization";
    private const string BasicSchemePrefix = "Basic ";
    private const string AdminPasswordEnvVar = "HONUA_ADMIN_PASSWORD";
    private const string AuthFailureMessageKey = "AuthFailureMessage";
    private const string AuthRealm = "Honua Admin";

    private readonly ApiKeyAuthenticationOptions _authOptions = dependencies?.Options ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IConnectionSecretResolver? _secretResolver = dependencies.SecretResolver;
    private readonly IAdminApiKeyStore? _adminApiKeyStore = dependencies.AdminApiKeyStore;

    /// <summary>
    /// Handles API key authentication with development bypass support
    /// </summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for development bypass modes
        if (IsDevelopmentBypassEnabled())
        {
            AuthenticationLog.DevelopmentBypassEnabled(Logger);
            return CreateSuccessfulAuthenticationResult("dev-bypass");
        }

        // Extract API key from explicit header, or from Basic auth compatibility mode.
        var providedApiKey = GetApiKeyFromHeader();
        if (string.IsNullOrEmpty(providedApiKey))
        {
            if (TryGetApiKeyFromBasicAuthorizationHeader(out providedApiKey, out var basicFailure))
            {
                // Basic auth compatibility successfully extracted the API key.
            }
            else if (!string.IsNullOrEmpty(basicFailure))
            {
                Context.Items[AuthFailureMessageKey] = basicFailure;
                await EmitAuditAsync("auth.failure", AuditOutcome.Failure, AuditActorType.Anonymous, AuditEvent.AnonymousActor).ConfigureAwait(false);
                return AuthenticateResult.Fail(basicFailure);
            }
        }

        if (string.IsNullOrEmpty(providedApiKey))
        {
            AuthenticationLog.NoApiKeyFound(Logger, ApiKeyHeader);
            return AuthenticateResult.NoResult();
        }

        if (_adminApiKeyStore is not null)
        {
            var storedKey = await _adminApiKeyStore.ValidateAsync(providedApiKey, Context.RequestAborted);
            if (storedKey is not null)
            {
                AuthenticationLog.ApiKeyAuthenticationSuccessful(Logger);
                await EmitAuditAsync("auth.success", AuditOutcome.Success, AuditActorType.ApiKey, storedKey.Record.Id.ToString("D")).ConfigureAwait(false);
                return CreateSuccessfulAuthenticationResult(
                    "admin-api-key",
                    storedKey.Record.Id,
                    storedKey.Record.Name,
                    storedKey.Record.Permissions);
            }
        }

        // Get configured admin password
        string? configuredPassword;
        try
        {
            configuredPassword = await ResolveAdminPasswordAsync(Context.RequestAborted);
        }
        catch (Exception ex)
        {
            AuthenticationLog.AdminPasswordResolutionFailed(Logger, ex);
            Context.Items[AuthFailureMessageKey] = "Admin authentication not configured";
            return AuthenticateResult.Fail("Admin authentication not configured");
        }
        if (string.IsNullOrEmpty(configuredPassword))
        {
            AuthenticationLog.NoAdminPasswordConfigured(Logger, AdminPasswordEnvVar);
            // Store the failure message for the challenge handler
            Context.Items[AuthFailureMessageKey] = "Admin authentication not configured";
            return AuthenticateResult.Fail("Admin authentication not configured");
        }

        // Perform constant-time comparison to prevent timing attacks
        if (!IsApiKeyValid(providedApiKey, configuredPassword))
        {
            AuthenticationLog.InvalidApiKeyProvided(Logger);
            await EmitAuditAsync("auth.failure", AuditOutcome.Failure, AuditActorType.Anonymous, AuditEvent.AnonymousActor).ConfigureAwait(false);
            return AuthenticateResult.Fail("Invalid API key");
        }

        AuthenticationLog.ApiKeyAuthenticationSuccessful(Logger);
        await EmitAuditAsync("auth.success", AuditOutcome.Success, AuditActorType.ApiKey, "admin-password").ConfigureAwait(false);
        return CreateSuccessfulAuthenticationResult("admin");
    }

    /// <summary>
    /// Best-effort emit of an authentication audit event. Failures inside the
    /// audit sink are intentionally swallowed — never block authentication on
    /// the audit log.
    /// </summary>
    private async Task EmitAuditAsync(string action, AuditOutcome outcome, AuditActorType actorType, string actor)
    {
        var auditLog = Context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authentication,
            Actor = actor,
            ActorType = actorType,
            ResourceType = "http",
            ResourceId = Context.Request.Path.HasValue ? Context.Request.Path.Value : null,
            Action = action,
            Outcome = outcome,
            CorrelationId = string.IsNullOrWhiteSpace(Context.TraceIdentifier)
                ? Guid.NewGuid().ToString("D")
                : Context.TraceIdentifier,
            RemoteIp = Context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Details = $"{{\"scheme\":\"api-key\",\"method\":\"{Context.Request.Method}\"}}",
        };

        try
        {
            await auditLog.RecordAsync(auditEvent, Context.RequestAborted).ConfigureAwait(false);
        }
        catch
        {
            // Per IAuditLog contract: audit failures must never block auth.
        }
    }

    private string? GetApiKeyFromHeader()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out StringValues apiKeyValues))
        {
            return null;
        }

        var providedApiKey = apiKeyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedApiKey))
        {
            AuthenticationLog.EmptyApiKeyProvided(Logger, ApiKeyHeader);
            return null;
        }

        return providedApiKey;
    }

    private bool TryGetApiKeyFromBasicAuthorizationHeader(out string? apiKey, out string? failureMessage)
    {
        apiKey = null;
        failureMessage = null;

        if (!_authOptions.EnableBasicAuthCompatibility)
        {
            return false;
        }

        if (!Request.Headers.TryGetValue(AuthorizationHeader, out var authHeaderValues))
        {
            return false;
        }

        var authorization = authHeaderValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith(BasicSchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_authOptions.RequireHttpsForBasicAuth && !IsHttpsRequest())
        {
            AuthenticationLog.BasicAuthRejectedInsecureTransport(Logger);
            failureMessage = "HTTP Basic authentication compatibility requires HTTPS.";
            return false;
        }

        var encoded = authorization[BasicSchemePrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            AuthenticationLog.InvalidBasicAuthorizationHeader(Logger);
            failureMessage = "Invalid HTTP Basic authorization header.";
            return false;
        }

        byte[] decodedBytes;
        try
        {
            decodedBytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            AuthenticationLog.InvalidBasicAuthorizationHeader(Logger);
            failureMessage = "Invalid HTTP Basic authorization header.";
            return false;
        }

        var decoded = Encoding.UTF8.GetString(decodedBytes);
        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex < 0 || separatorIndex == decoded.Length - 1)
        {
            AuthenticationLog.InvalidBasicAuthorizationHeader(Logger);
            failureMessage = "Invalid HTTP Basic authorization header.";
            return false;
        }

        var password = decoded[(separatorIndex + 1)..];
        if (string.IsNullOrWhiteSpace(password))
        {
            AuthenticationLog.InvalidBasicAuthorizationHeader(Logger);
            failureMessage = "Invalid HTTP Basic authorization header.";
            return false;
        }

        AuthenticationLog.BasicAuthCompatibilityUsed(Logger);
        apiKey = password;
        return true;
    }

    private bool IsHttpsRequest()
    {
        // Request.IsHttps reflects direct TLS and trusted forwarded HTTPS once ForwardedHeaders middleware has run.
        return Request.IsHttps;
    }

    /// <summary>
    /// Determines if development authentication bypass is enabled
    /// </summary>
    private bool IsDevelopmentBypassEnabled()
    {
        // SECURITY: Never allow bypass in production, regardless of configuration
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            AuthenticationLog.DevelopmentBypassBlockedInProduction(Logger);
            return false;
        }

        // Check if HONUA_DEV_AUTH is explicitly set to true
        string? devAuthBypass = _authOptions.DevAuthBypass;
        if (string.Equals(devAuthBypass, "true", StringComparison.OrdinalIgnoreCase))
        {
            // Only allow in Test environment, not just any non-production environment
            if (_authOptions.IsTestMode)
            {
                AuthenticationLog.DevelopmentBypassEnabled(Logger);
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
        byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
        byte[] configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        var maxLength = Math.Max(providedBytes.Length, configuredBytes.Length);
        byte[]? providedRental = null;
        byte[]? configuredRental = null;
        Span<byte> paddedProvided = maxLength <= 256
            ? stackalloc byte[maxLength]
            : (providedRental = ArrayPool<byte>.Shared.Rent(maxLength)).AsSpan(0, maxLength);
        Span<byte> paddedConfigured = maxLength <= 256
            ? stackalloc byte[maxLength]
            : (configuredRental = ArrayPool<byte>.Shared.Rent(maxLength)).AsSpan(0, maxLength);

        paddedProvided.Clear();
        paddedConfigured.Clear();

        providedBytes.CopyTo(paddedProvided);
        configuredBytes.CopyTo(paddedConfigured);

        var matches = CryptographicOperations.FixedTimeEquals(paddedProvided, paddedConfigured);

        CryptographicOperations.ZeroMemory(paddedProvided);
        CryptographicOperations.ZeroMemory(paddedConfigured);

        if (providedRental != null)
        {
            ArrayPool<byte>.Shared.Return(providedRental, clearArray: false);
        }

        if (configuredRental != null)
        {
            ArrayPool<byte>.Shared.Return(configuredRental, clearArray: false);
        }

        return matches && providedBytes.Length == configuredBytes.Length;
    }

    /// <summary>
    /// Creates a successful authentication result with admin claims
    /// </summary>
    private AuthenticateResult CreateSuccessfulAuthenticationResult(
        string authenticationType,
        Guid? apiKeyId = null,
        string? apiKeyName = null,
        IReadOnlyList<string>? permissions = null)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("auth_type", authenticationType)
        };

        if (apiKeyId.HasValue)
        {
            claims.Add(new Claim("api_key_id", apiKeyId.Value.ToString("D")));
        }

        if (!string.IsNullOrWhiteSpace(apiKeyName))
        {
            claims.Add(new Claim("api_key_name", apiKeyName));
        }

        if (permissions is not null)
        {
            foreach (var permission in permissions)
            {
                claims.Add(new Claim("permission", permission));
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    private async Task<string?> ResolveAdminPasswordAsync(CancellationToken cancellationToken)
    {
        var configuredPassword = _authOptions.AdminPassword;
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            return null;
        }

        if (_secretResolver == null)
        {
            return configuredPassword;
        }

        var canResolve = await _secretResolver.CanResolveSecretAsync(configuredPassword, cancellationToken);
        if (!canResolve)
        {
            return configuredPassword;
        }

        return await _secretResolver.ResolveConnectionStringAsync(configuredPassword, cancellationToken);
    }

    /// <summary>
    /// Handles authentication challenges by returning 401 Unauthorized
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.Append("WWW-Authenticate", $"ApiKey realm=\"{AuthRealm}\", header=\"{ApiKeyHeader}\"");
        if (_authOptions.EnableBasicAuthCompatibility)
        {
            Response.Headers.Append("WWW-Authenticate", $"Basic realm=\"{AuthRealm}\", charset=\"UTF-8\"");
        }

        // Check if there's a specific failure message from authentication
        string? failureMessage = Context.Items[AuthFailureMessageKey] as string;
        bool devBypassEnabled = IsDevelopmentBypassEnabled();
        var detail = !string.IsNullOrEmpty(failureMessage)
            ? failureMessage
            : devBypassEnabled
                ? "API key required. Development bypass is enabled but this request still requires authentication."
                : "API key required. Provide a valid API key in the X-API-Key header.";

        return StandardErrorResponseFormatter.WriteErrorAsync(
            Context,
            StandardErrorResponse.Unauthorized(detail));
    }
}
