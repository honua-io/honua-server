// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Resolves a provider API key. Resolution order, mirroring the NlQuery key handling:
/// <list type="number">
///   <item>If the configured value is a secret reference, resolve it via <see cref="ISecretProvider"/>.</item>
///   <item>If a plain configured value is present, use it.</item>
///   <item>Otherwise fall back to the per-provider environment variable
///   (for example <c>HONUA_WORKFLOWGEN_OPENAI_API_KEY</c>).</item>
/// </list>
/// </summary>
internal sealed class WorkflowGenerationApiKeyResolver
{
    private readonly ISecretProvider? _secretProvider;

    public WorkflowGenerationApiKeyResolver(ISecretProvider? secretProvider = null)
    {
        _secretProvider = secretProvider;
    }

    /// <summary>Builds the per-provider env var name, for example HONUA_WORKFLOWGEN_OPENAI_API_KEY.</summary>
    public static string EnvVarName(string providerId)
        => $"HONUA_WORKFLOWGEN_{providerId.ToUpperInvariant()}_API_KEY";

    /// <summary>Resolves the API key for the supplied provider, or empty when none is configured.</summary>
    public async Task<string> ResolveAsync(
        string providerId,
        WorkflowGenerationProviderOptions options,
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

        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName(providerId));
        return string.IsNullOrWhiteSpace(fromEnv) ? string.Empty : fromEnv;
    }
}
