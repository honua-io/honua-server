// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Resolves and validates the currently configured bootstrap administrator password.
/// </summary>
internal static class AdminPasswordResolver
{
    public static async Task<string?> ResolveAsync(
        ApiKeyAuthenticationOptions options,
        IConnectionSecretResolver? secretResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuredPassword = options.AdminPassword;
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            return null;
        }

        var resolvedPassword = configuredPassword;
        if (secretResolver is not null &&
            await secretResolver.CanResolveSecretAsync(configuredPassword, cancellationToken).ConfigureAwait(false))
        {
            resolvedPassword = await secretResolver
                .ResolveConnectionStringAsync(configuredPassword, cancellationToken)
                .ConfigureAwait(false);
        }

        AdminPasswordValidation.ValidateRefreshedPassword(resolvedPassword, options.EnvironmentName);
        return resolvedPassword;
    }
}
