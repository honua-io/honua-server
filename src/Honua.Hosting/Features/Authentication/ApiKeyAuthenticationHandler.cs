// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Authentication;

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
    private const string AdminAuthenticationNotConfigured = "Admin authentication not configured";
    private static readonly Guid DevelopmentBypassActorId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid BootstrapAdminActorId = new("00000000-0000-0000-0000-000000000002");

    private readonly ApiKeyAuthenticationDependencies _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly ApiKeyAuthenticationOptions _authOptions = dependencies.Options;

    /// <summary>
    /// Handles API key authentication with development bypass support
    /// </summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for development bypass modes
        if (IsDevelopmentBypassEnabled())
        {
            AuthenticationLog.DevelopmentBypassEnabled(Logger);
            return CreateSuccessfulAuthenticationResult(Scheme.Name, "dev-bypass", DevelopmentBypassActorId);
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
                return AuthenticateResult.Fail(basicFailure);
            }
        }

        if (string.IsNullOrEmpty(providedApiKey))
        {
            AuthenticationLog.NoApiKeyFound(Logger, ApiKeyHeader);
            return AuthenticateResult.NoResult();
        }

        var validation = await ValidateApiKeyAsync(
            providedApiKey,
            Scheme.Name,
            _dependencies,
            Logger,
            Context.RequestAborted);
        switch (validation.Rejection)
        {
            case ApiKeyRejection.None:
                return validation.Success!;
            case ApiKeyRejection.AdminPasswordNotConfigured:
                AuthenticationLog.NoAdminPasswordConfigured(Logger, AdminPasswordEnvVar);
                // Store the failure message for the challenge handler
                Context.Items[AuthFailureMessageKey] = AdminAuthenticationNotConfigured;
                return AuthenticateResult.Fail(AdminAuthenticationNotConfigured);
            case ApiKeyRejection.AdminPasswordUnavailable:
                Context.Items[AuthFailureMessageKey] = AdminAuthenticationNotConfigured;
                return AuthenticateResult.Fail(AdminAuthenticationNotConfigured);
            default:
                AuthenticationLog.InvalidApiKeyProvided(Logger);
                return AuthenticateResult.Fail("Invalid API key");
        }
    }

    /// <summary>
    /// Validates a presented API key against the managed key store and the bootstrap
    /// admin credential, and projects the key's own authority onto a principal.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="PortalTokenAuthenticationHandler"/> so a key presented through
    /// a GeoServices token transport (#5492) yields exactly the principal the
    /// <c>X-API-Key</c> header yields: same roles, permission claims and credential kind.
    /// Rejections are returned rather than logged so each caller reports its own transport.
    /// </remarks>
    internal static async Task<ApiKeyValidation> ValidateApiKeyAsync(
        string providedApiKey,
        string schemeName,
        ApiKeyAuthenticationDependencies dependencies,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        if (dependencies.AdminApiKeyStore is not null)
        {
            var storedKey = await dependencies.AdminApiKeyStore.ValidateAsync(providedApiKey, cancellationToken);
            if (storedKey is not null)
            {
                AuthenticationLog.ApiKeyAuthenticationSuccessful(logger);
                return new ApiKeyValidation(
                    CreateSuccessfulAuthenticationResult(
                        schemeName,
                        "admin-api-key",
                        storedKey.Record.Id,
                        storedKey.Record.Name,
                        storedKey.Record.Permissions),
                    ApiKeyRejection.None);
            }
        }

        // Get configured admin password
        string? configuredPassword;
        try
        {
            configuredPassword = await ResolveAdminPasswordAsync(dependencies, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional: secret resolution can fan out to heterogeneous cloud SDKs (AWS
            // Secrets Manager, Azure Key Vault, etc.) with provider-specific exception types;
            // this boundary must fail the request safely rather than crash, and it already logs.
            AuthenticationLog.AdminPasswordResolutionFailed(logger, ex);
            return new ApiKeyValidation(null, ApiKeyRejection.AdminPasswordUnavailable);
        }
        if (string.IsNullOrEmpty(configuredPassword))
        {
            return new ApiKeyValidation(null, ApiKeyRejection.AdminPasswordNotConfigured);
        }

        // Perform constant-time comparison to prevent timing attacks
        if (!IsApiKeyValid(providedApiKey, configuredPassword))
        {
            return new ApiKeyValidation(null, ApiKeyRejection.InvalidKey);
        }

        AuthenticationLog.ApiKeyAuthenticationSuccessful(logger);
        return new ApiKeyValidation(
            CreateSuccessfulAuthenticationResult(schemeName, "admin", BootstrapAdminActorId),
            ApiKeyRejection.None);
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

        // codeql[cs/user-controlled-bypass]: the transport check uses Request.IsHttps, which
        // reflects the real connection scheme (forwarded values are honored only after the
        // trusted-proxy ForwardedHeaders middleware), so it is not attacker-controllable;
        // this branch is the secure path that REJECTS Basic auth over non-HTTPS transport.
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
    /// Determines if development authentication bypass is enabled.
    /// </summary>
    /// <remarks>
    /// SECURITY: The bypass is gated by three independent conditions and is only
    /// active when all three are satisfied. Any deployment that does not match
    /// every condition - including Staging, QA, Production, or any other custom
    /// environment - falls through to normal API-key authentication.
    /// <list type="number">
    ///   <item>Resolved ASPNETCORE_ENVIRONMENT is exactly "Test" (the only
    ///   environment where the in-process test host runs unauthenticated).
    ///   We deliberately do NOT honour the bypass in "Development" because
    ///   developers should exercise the same API-key path as production.</item>
    ///   <item><c>HONUA_DEV_AUTH=true</c> is supplied.</item>
    ///   <item><c>HONUA_DEV_AUTH_ALLOW_BYPASS=true</c> is supplied as an
    ///   independent operator acknowledgement so an accidentally-leaked
    ///   HONUA_DEV_AUTH cannot, on its own, disable authentication.</item>
    /// </list>
    /// </remarks>
    private bool IsDevelopmentBypassEnabled()
    {
        // SECURITY: Never allow bypass in production or any non-Test environment.
        // We rely on the environment name captured at startup via
        // builder.Environment.EnvironmentName (immutable options binding). Reading
        // the live process env var was a footgun: ASP.NET hosting can configure
        // its environment via UseEnvironment(...) without touching the OS env,
        // so a process-env read would disagree with the captured option in tests.
        var startupEnvironment = _authOptions.EnvironmentName;
        if (string.Equals(startupEnvironment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            // Normal Production requests are not bypass attempts. Warn only
            // when the operator has explicitly configured the bypass opt-in.
            if (string.Equals(_authOptions.DevAuthBypass, "true", StringComparison.OrdinalIgnoreCase))
            {
                AuthenticationLog.DevelopmentBypassBlockedInProduction(Logger);
            }

            return false;
        }

        if (!IsAllowedBypassEnvironment(startupEnvironment))
        {
            // Staging, QA, custom envs, anything other than Test/Development must fall through.
            return false;
        }

        // Belt-and-braces: the captured IsTestMode flag must also agree.
        if (!_authOptions.IsTestMode)
        {
            return false;
        }

        // Require the explicit opt-in token AND the operator acknowledgement.
        if (!string.Equals(_authOptions.DevAuthBypass, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_authOptions.DevAuthBypassAcknowledged, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AuthenticationLog.DevelopmentBypassEnabled(Logger);
        return true;
    }

    private static bool IsAllowedBypassEnvironment(string? environmentName)
    {
        // Only "Test" is allowed - Development, Staging, QA, Production, and any
        // other custom environment fall through to standard API-key auth.
        return string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase);
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
    private static AuthenticateResult CreateSuccessfulAuthenticationResult(
        string schemeName,
        string authenticationType,
        Guid? apiKeyId = null,
        string? apiKeyName = null,
        IReadOnlyList<string>? permissions = null)
    {
        // A key whose grants describe a layer-scoped write credential (#1637) is
        // authenticated as a NON-admin principal: it never receives the admin role
        // and so cannot satisfy the admin authorization policy guarding admin
        // endpoints. Its write authority is enforced by the shared
        // ServiceDataEditorAuthorization pipeline against the scope claims below.
        var isScopedWriteKey = LayerScopedWriteKey.IsScopedWriteKey(permissions);

        // A key is granted the blanket "admin" role only when its grants confer full
        // administrative authority (issue #1985). The password-based bootstrap admin
        // and the development bypass authenticate with null permissions and so remain
        // full admins; an unscoped key (or one carrying admin / * / admin:* grants)
        // likewise passes every admin endpoint. A key whose grants are genuinely
        // scoped never silently passes RequireRole("admin"). Narrow administrative
        // grants receive a separate role admitted only by permission-checked policies;
        // ordinary scoped keys retain only their own endpoint-level authority.
        var confersFullAdmin = LayerScopedWriteKey.ConfersFullAdmin(permissions);
        var isApprovedOperationKey = permissions?.Any(AdminApiKeyPermission.IsApprovedOperationGrant) == true;
        var hasAdministrativeGrant = permissions?.Any(AdminApiKeyPermission.IsAdministrativeGrant) == true;
        List<Claim> claims;
        if (isScopedWriteKey)
        {
            claims =
            [
                new Claim(ClaimTypes.Name, apiKeyName ?? "layer-write-key"),
                new Claim(ClaimTypes.Role, LayerScopedWriteKey.Role),
                new Claim("auth_type", LayerScopedWriteKey.AuthType),
                new Claim(LayerScopedWriteKey.ScopeClaimType, LayerScopedWriteKey.AuthType),
            ];
        }
        else if (isApprovedOperationKey)
        {
            // Only persisted server-minted grants can supply this binding. Never
            // promote the caller-controlled tenant header into authenticated claims.
            var tenantBindings = permissions!
                .Where(permission => permission.StartsWith(AdminApiKeyPermission.ApprovedOperationTenantGrantPrefix, StringComparison.Ordinal))
                .Select(permission => permission[AdminApiKeyPermission.ApprovedOperationTenantGrantPrefix.Length..])
                .ToArray();
            if (tenantBindings.Length != 1)
            {
                return AuthenticateResult.Fail("Approved operation credential has no unambiguous tenant binding.");
            }

            var tenantClaim = new Claim(AdminApiKeyPermission.ApprovedOperationTenantClaim, tenantBindings[0]);
            // This binding comes from the persisted server-minted credential, including
            // an explicit empty binding. Preserve it through OIDC claim sanitization.
            tenantClaim.Properties[CanonicalSecurityActor.FrameworkOwnedClaimProperty] = bool.TrueString;
            claims =
            [
                new Claim(ClaimTypes.Name, apiKeyName ?? "approved-operation"),
                new Claim(ClaimTypes.Role, AdminApiKeyPermission.ApprovedOperationRole),
                tenantClaim,
                new Claim("auth_type", authenticationType),
            ];
        }
        else if (confersFullAdmin)
        {
            claims =
            [
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", authenticationType),
            ];
        }
        else if (hasAdministrativeGrant)
        {
            claims =
            [
                new Claim(ClaimTypes.Name, apiKeyName ?? "scoped-admin-key"),
                new Claim(ClaimTypes.Role, AdminApiKeyPermission.ScopedAdminRole),
                new Claim("auth_type", authenticationType),
            ];
        }
        else
        {
            // Genuinely scoped, non-admin, non-write key: authenticated but NOT an
            // admin. It carries only its scoped "permission" claims (added below) and
            // is denied any endpoint guarded by the admin role.
            claims =
            [
                new Claim(ClaimTypes.Name, apiKeyName ?? "scoped-api-key"),
                new Claim(ClaimTypes.Role, LayerScopedWriteKey.ScopedKeyRole),
                new Claim("auth_type", authenticationType),
            ];
        }

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

        var identity = new ClaimsIdentity(claims, schemeName);

        // Every authority claim above comes from the persisted key record (or the bootstrap
        // password / dev-bypass branches), so mark them framework-owned: shared claim
        // sanitization keeps stamped copies and drops any that arrived in a token.
        CanonicalSecurityActor.StampAuthorityClaims(identity);

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, schemeName);

        return AuthenticateResult.Success(ticket);
    }

    private static async Task<string?> ResolveAdminPasswordAsync(
        ApiKeyAuthenticationDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var configuredPassword = dependencies.Options.AdminPassword;
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            return null;
        }

        var resolvedPassword = configuredPassword;
        if (dependencies.SecretResolver is { } secretResolver)
        {
            var canResolve = await secretResolver.CanResolveSecretAsync(configuredPassword, cancellationToken);
            if (canResolve)
            {
                resolvedPassword = await secretResolver.ResolveConnectionStringAsync(configuredPassword, cancellationToken);
            }
        }

        AdminPasswordValidation.ValidateRefreshedPassword(resolvedPassword, dependencies.Options.EnvironmentName);
        return resolvedPassword;
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

/// <summary>
/// Why <see cref="ApiKeyAuthenticationHandler.ValidateApiKeyAsync"/> refused a presented key.
/// </summary>
internal enum ApiKeyRejection
{
    /// <summary>The key was accepted.</summary>
    None,

    /// <summary>The key matched no managed key and the bootstrap admin credential.</summary>
    InvalidKey,

    /// <summary>The key matched no managed key and no bootstrap admin credential is configured.</summary>
    AdminPasswordNotConfigured,

    /// <summary>The key matched no managed key and the bootstrap admin credential could not be resolved.</summary>
    AdminPasswordUnavailable,
}

/// <summary>
/// Outcome of <see cref="ApiKeyAuthenticationHandler.ValidateApiKeyAsync"/>: the success
/// result carrying the key's own principal, or the reason it was refused.
/// </summary>
internal readonly record struct ApiKeyValidation(AuthenticateResult? Success, ApiKeyRejection Rejection);
