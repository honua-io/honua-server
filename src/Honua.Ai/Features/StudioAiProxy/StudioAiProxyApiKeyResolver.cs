// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// Resolves a Studio AI proxy provider's API key, in this order:
/// <list type="number">
///   <item>If the configured value is a secret reference, resolve it via <see cref="ISecretProvider"/>.</item>
///   <item>Otherwise use the plain configured value when present.</item>
///   <item>Otherwise fall back to the per-provider environment variable
///   (for example <c>HONUA_STUDIOAI_MYPROVIDER_API_KEY</c>).</item>
/// </list>
/// Credentials never leave the server: this resolver's output is only ever attached to the
/// outbound provider request, never echoed back to the Studio client.
/// </summary>
public sealed class StudioAiProxyApiKeyResolver
{
    /// <summary>Stable error code returned when configured provider credentials are unavailable.</summary>
    public const string CredentialUnavailableCode = "studio_ai/provider_credential_unavailable";

    private readonly ISecretProvider? _secretProvider;

    public StudioAiProxyApiKeyResolver(ISecretProvider? secretProvider = null)
    {
        _secretProvider = secretProvider;
    }

    /// <summary>Builds the per-provider env var name, for example HONUA_STUDIOAI_MYPROVIDER_API_KEY.</summary>
    public static string EnvVarName(string providerName)
        => $"HONUA_STUDIOAI_{providerName.ToUpperInvariant()}_API_KEY";

    /// <summary>Resolves the API key for the supplied provider, or empty when none is configured.</summary>
    public async Task<string> ResolveAsync(
        string providerName,
        StudioAiProxyProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        var configured = options.ApiKey;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var isReference = _secretProvider?.IsSecretReference(configured) == true;
            if (isReference)
            {
                try
                {
                    var resolved = await _secretProvider!
                        .GetSecretOrDefaultAsync(configured, defaultValue: null, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Collapse provider details: exception messages can contain the reference or value.
                }

                throw new StudioAiProxyCredentialUnavailableException();
            }

            if (IsLoopbackEndpoint(options.Endpoint))
            {
                return configured;
            }

            throw new StudioAiProxyCredentialUnavailableException();
        }

        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName(providerName));
        return string.IsNullOrWhiteSpace(fromEnv) ? string.Empty : fromEnv;
    }

    private static bool IsLoopbackEndpoint(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
}

internal sealed class StudioAiProxyCredentialUnavailableException : Exception
{
    public StudioAiProxyCredentialUnavailableException()
        : base("Provider credentials are unavailable.")
    {
    }
}
