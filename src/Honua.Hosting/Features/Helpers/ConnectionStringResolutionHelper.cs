// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Infrastructure.Helpers;

internal static class ConnectionStringResolutionHelper
{
    public static Task<string?> ResolveDefaultConnectionStringAsync(
        IConfiguration configuration,
        IConnectionSecretResolver? secretResolver,
        CancellationToken cancellationToken = default)
        => ResolveConnectionStringValueAsync(
            configuration.GetConnectionString("DefaultConnection"),
            "ConnectionStrings:DefaultConnection",
            secretResolver,
            cancellationToken);

    /// <summary>
    /// Resolves a raw connection-string value through the same env:-reference-then-secret-resolver-chain
    /// mechanism <see cref="ResolveDefaultConnectionStringAsync"/> uses for
    /// <c>ConnectionStrings:DefaultConnection</c>, generalized so other connection strings that must go
    /// through the identical resolution order (currently Redis — honua-server#3011) can reuse it instead
    /// of duplicating it: an <c>env:VARIABLE_NAME</c> reference is substituted first, then the remaining
    /// value is handed to <paramref name="secretResolver"/> (e.g. the AWS Secrets Manager / Azure Key
    /// Vault-backed <c>IConnectionSecretResolver</c> chain) only if it reports it can resolve the value;
    /// a plain value, or a resolver-less caller, passes the value through unchanged.
    /// </summary>
    /// <param name="connectionString">The raw configured value (may be null/empty, an <c>env:</c>
    /// reference, a resolver-backed reference such as <c>aws:secretsmanager:&lt;arn&gt;</c>, or a plain
    /// connection string).</param>
    /// <param name="settingName">The configuration key, used only in the error message if an <c>env:</c>
    /// reference names a variable that is not set.</param>
    /// <param name="secretResolver">The resolver chain to try for non-<c>env:</c> references, or
    /// <see langword="null"/> to skip resolver-backed resolution entirely.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<string?> ResolveConnectionStringValueAsync(
        string? connectionString,
        string settingName,
        IConnectionSecretResolver? secretResolver,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        connectionString = SecretReferenceResolver.ResolveEnvironmentReference(connectionString, settingName);

        if (string.IsNullOrWhiteSpace(connectionString) || secretResolver is null)
        {
            return connectionString;
        }

        if (!await secretResolver.CanResolveSecretAsync(connectionString, cancellationToken).ConfigureAwait(false))
        {
            return connectionString;
        }

        return await secretResolver.ResolveConnectionStringAsync(connectionString, cancellationToken).ConfigureAwait(false);
    }
}
