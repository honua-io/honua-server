// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Validates the development authentication bypass configuration once at host startup.
/// </summary>
/// <remarks>
/// SECURITY: This validator is the primary line of defence against the bypass being
/// activated by misconfiguration. Production deploys that smuggle in
/// <c>HONUA_DEV_AUTH=true</c> will fail to start, and developers whose attempted
/// bypass is silently rejected (wrong env, missing ack) get a startup warning so
/// they understand why their admin requests are still being challenged.
/// </remarks>
public static class DevAuthBypassStartupValidator
{
    /// <summary>
    /// Validates the dev-auth bypass configuration.
    /// </summary>
    /// <param name="environmentName">The current ASP.NET Core environment name.</param>
    /// <param name="devAuthBypass">The raw <c>HONUA_DEV_AUTH</c> configuration value.</param>
    /// <param name="devAuthBypassAck">The raw <c>HONUA_DEV_AUTH_ACK</c> configuration value.</param>
    /// <param name="loggerFactory">Logger factory used to emit warnings/errors.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="environmentName"/> is "Production" and
    /// <paramref name="devAuthBypass"/> equals <c>true</c>.
    /// </exception>
    public static void Validate(
        string? environmentName,
        string? devAuthBypass,
        string? devAuthBypassAck,
        ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger(typeof(DevAuthBypassStartupValidator).FullName!);

        var bypassRequested = string.Equals(devAuthBypass, "true", StringComparison.OrdinalIgnoreCase);
        if (!bypassRequested)
        {
            // No opt-in attempted; nothing to validate.
            return;
        }

        // FATAL: HONUA_DEV_AUTH=true must NEVER appear in a Production deploy.
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
        {
            if (logger is not null)
            {
                AuthenticationLog.DevelopmentBypassInProductionFatal(logger);
            }

            throw new InvalidOperationException(
                "SECURITY: HONUA_DEV_AUTH=true is not permitted when ASPNETCORE_ENVIRONMENT=Production. " +
                "Remove the variable from the deployment or correct the environment name. " +
                "If you believe this is a misconfigured staging deploy, this error is exactly the safety " +
                "net it was designed to be.");
        }

        // Bypass was requested; warn if it will NOT actually activate so developers
        // are not surprised by ongoing 401 responses.
        if (!string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase))
        {
            if (logger is not null)
            {
                AuthenticationLog.DevelopmentBypassRejected(
                    logger,
                    $"ASPNETCORE_ENVIRONMENT='{environmentName}' is not 'Test'. " +
                    "The bypass only activates when the environment is exactly 'Test'.");
            }
            return;
        }

        if (!string.Equals(
                devAuthBypassAck,
                ApiKeyAuthenticationOptions.ExpectedDevAuthBypassAck,
                StringComparison.Ordinal))
        {
            if (logger is not null)
            {
                AuthenticationLog.DevelopmentBypassRejected(
                    logger,
                    $"HONUA_DEV_AUTH_ACK is not set to the required acknowledgement value " +
                    $"('{ApiKeyAuthenticationOptions.ExpectedDevAuthBypassAck}'). " +
                    "This token must be set verbatim to opt in to the bypass.");
            }
            return;
        }

        // All conditions satisfied; the per-request handler will activate the bypass.
    }
}
