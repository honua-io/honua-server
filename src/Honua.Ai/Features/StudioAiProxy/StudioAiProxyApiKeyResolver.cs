// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Ai.StudioAiProxy;

// The resolved key is attached only to the outbound provider request and is never echoed to the client.
public sealed class StudioAiProxyApiKeyResolver
{
    public const string CredentialUnavailableCode = "studio_ai/provider_credential_unavailable";

    private readonly ISecretProvider? _secretProvider;

    public StudioAiProxyApiKeyResolver(ISecretProvider? secretProvider = null)
    {
        _secretProvider = secretProvider;
    }

    public static string EnvVarName(string providerName)
        => $"HONUA_STUDIOAI_{providerName.ToUpperInvariant()}_API_KEY";

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

            if (options.ApiKeyFromEnvironment || IsLoopbackEndpoint(options.Endpoint))
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
