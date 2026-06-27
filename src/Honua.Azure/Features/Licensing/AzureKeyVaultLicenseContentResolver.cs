// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Logging;

// NOTE: namespace deliberately omits the "Azure" segment (it uses Honua.Licensing, not
// Honua.Azure.Features.Licensing) to match the existing Honua.Azure convention (Honua.Alerts,
// Honua.FileStorage, Honua.ControlPlane). A Honua.Azure namespace would shadow the global Azure
// SDK namespace for callers that reference Azure.* by its fully-qualified name.
namespace Honua.Licensing;

/// <summary>
/// Resolves a signed license envelope from Azure Key Vault. Confined to
/// <c>Honua.Azure</c> so the Azure.Security.KeyVault.Secrets surface stays out of the
/// cloud-neutral <c>Honua.Hosting</c> licensing pipeline (cloud-SDK isolation
/// contract, <c>CloudSdkIsolationTests</c>). It is the Azure-side parity of the AWS
/// Secrets Manager resolver. Activated when
/// <c>Licensing:LicenseContentSecretRef=azure:keyvault:&lt;vault-uri&gt;/&lt;secret&gt;</c>
/// is configured; on Azure Functions / Container Apps this lets the ~2KB envelope be
/// delivered via a Key Vault reference instead of an environment variable.
/// </summary>
internal sealed class AzureKeyVaultLicenseContentResolver : ILicenseContentSecretResolver
{
    private const string KeyVaultPrefix = "azure:keyvault:";

    private readonly Func<Uri, SecretClient> _clientFactory;
    private readonly ILogger<AzureKeyVaultLicenseContentResolver> _logger;

    /// <summary>
    /// Creates the resolver with the default <see cref="SecretClient"/> factory backed by
    /// <see cref="DefaultAzureCredential"/> (managed identity, environment, workload-identity,
    /// or developer credentials). This is the constructor the DI container selects.
    /// </summary>
    /// <param name="logger">The resolver logger.</param>
    public AzureKeyVaultLicenseContentResolver(ILogger<AzureKeyVaultLicenseContentResolver> logger)
        : this(logger, clientFactory: null)
    {
    }

    /// <summary>
    /// Test seam: allows injecting a <see cref="SecretClient"/> factory so the secret fetch can be
    /// exercised without a live Key Vault.
    /// </summary>
    /// <param name="logger">The resolver logger.</param>
    /// <param name="clientFactory">
    /// Factory that builds a <see cref="SecretClient"/> for a given vault URI. When <c>null</c> the
    /// default <see cref="DefaultAzureCredential"/>-backed client is used.
    /// </param>
    internal AzureKeyVaultLicenseContentResolver(
        ILogger<AzureKeyVaultLicenseContentResolver> logger,
        Func<Uri, SecretClient>? clientFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clientFactory = clientFactory ?? (static uri => new SecretClient(uri, new DefaultAzureCredential()));
    }

    /// <inheritdoc />
    public bool CanResolve(string? secretReference)
        => TryParseReference(secretReference, out _, out _, out _);

    /// <inheritdoc />
    public async Task<string?> ResolveLicenseContentAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseReference(secretReference, out var vaultUri, out var secretName, out var secretVersion))
        {
            return null;
        }

        try
        {
            var client = _clientFactory(vaultUri);
            Response<KeyVaultSecret> response = await client
                .GetSecretAsync(secretName, secretVersion, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var value = response.Value?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                AzureLicenseLog.SecretEmpty(_logger);
                return null;
            }

            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail safe: the license pipeline degrades to Community when the secret cannot be
            // fetched (missing RBAC grant, wrong vault, deleted secret, unreachable endpoint, etc.).
            AzureLicenseLog.SecretFetchFailed(_logger, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Parses a Key Vault license reference into the vault endpoint, secret name, and optional secret
    /// version. Two forms are accepted:
    /// <list type="bullet">
    /// <item>
    /// The canonical Azure Key Vault secret identifier,
    /// <c>azure:keyvault:https://&lt;vault&gt;.vault.azure.net/secrets/&lt;name&gt;</c> (with an optional
    /// trailing <c>/&lt;version&gt;</c>). This is the form Azure surfaces in the portal and CLI.
    /// </item>
    /// <item>
    /// The documented shorthand,
    /// <c>azure:keyvault:https://&lt;vault&gt;.vault.azure.net/&lt;secret&gt;</c>.
    /// </item>
    /// </list>
    /// The vault endpoint is always derived from the URI scheme + authority — never by string-splitting
    /// the path — because a naive split on the final <c>'/'</c> folds the <c>/secrets</c> segment into the
    /// vault base, producing an endpoint that silently 404s every fetch (so Pro never activates).
    /// Returns <c>false</c> (rather than throwing) for any unsupported or malformed reference so both
    /// <see cref="CanResolve"/> and the fail-safe resolution path stay non-throwing.
    /// </summary>
    private static bool TryParseReference(
        string? secretReference,
        out Uri vaultUri,
        out string secretName,
        out string? secretVersion)
    {
        vaultUri = null!;
        secretName = string.Empty;
        secretVersion = null;

        if (string.IsNullOrWhiteSpace(secretReference) ||
            !secretReference.StartsWith(KeyVaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = secretReference[KeyVaultPrefix.Length..].Trim();
        if (remainder.Length == 0 ||
            !Uri.TryCreate(remainder, UriKind.Absolute, out var parsedUri) ||
            parsedUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // Vault endpoint = scheme + authority only (e.g. https://myvault.vault.azure.net/). The secret
        // name and optional version come from the path segments, NOT from a trailing-slash split.
        var vaultEndpoint = new Uri(parsedUri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        var segments = parsedUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Canonical identifier: /secrets/<name>[/<version>]
        if (segments.Length >= 2 &&
            string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(segments[1]))
            {
                return false;
            }

            vaultUri = vaultEndpoint;
            secretName = segments[1];
            secretVersion = segments.Length >= 3 && !string.IsNullOrWhiteSpace(segments[2])
                ? segments[2]
                : null;
            return true;
        }

        // Shorthand: https://<vault>.vault.azure.net/<secret>
        if (segments.Length == 1 && !string.IsNullOrWhiteSpace(segments[0]))
        {
            vaultUri = vaultEndpoint;
            secretName = segments[0];
            return true;
        }

        return false;
    }
}
