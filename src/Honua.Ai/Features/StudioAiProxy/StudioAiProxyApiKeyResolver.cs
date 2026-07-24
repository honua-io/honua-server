// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// Resolves a Studio AI proxy provider's API key. Mirrors <c>WorkflowGenerationApiKeyResolver</c>'s
/// resolution order:
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

        if (!string.IsNullOrWhiteSpace(configured)
            && _secretProvider is not null
            && _secretProvider.IsSecretReference(configured))
        {
            var resolved = await _secretProvider
                .GetSecretOrDefaultAsync(configured, defaultValue: null, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName(providerName));
        return string.IsNullOrWhiteSpace(fromEnv) ? string.Empty : fromEnv;
    }
}
