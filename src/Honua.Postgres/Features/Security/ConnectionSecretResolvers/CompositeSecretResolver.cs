// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Security.ConnectionSecretResolvers;

/// <summary>
/// Composite secret resolver that delegates to specific providers based on secret reference format.
/// </summary>
/// <remarks>
/// This resolver examines the provider prefix in secret references and delegates
/// to the appropriate provider-specific resolver. Supports multiple secret management
/// systems in the same application.
///
/// Secret reference format: {provider}:{path}
/// Examples:
/// - "env:DATABASE_CONNECTION_STRING"
/// - "aws:secretsmanager:prod-database-credentials"
/// - "azure:keyvault:my-vault:database-connection"
/// - "gcp:secretmanager:my-project:database-connection"
/// </remarks>
internal sealed class CompositeSecretResolver : IConnectionSecretResolver
{
    private readonly Dictionary<string, IConnectionSecretResolver> _resolvers;
    private readonly ILogger<CompositeSecretResolver> _logger;

    // Logger message delegates for performance
    private static readonly Action<ILogger, string, Exception?> _logDuplicateResolver =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(_logDuplicateResolver)),
            "Duplicate secret resolver registered for provider '{Provider}'. Using first registration.");

    private static readonly Action<ILogger, string, string, Exception?> _logResolverRegistered =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(2, nameof(_logResolverRegistered)),
            "Registered secret resolver for provider '{Provider}': {ResolverType}");

    private static readonly Action<ILogger, Exception?> _logNoResolversRegistered =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, nameof(_logNoResolversRegistered)),
            "No secret resolvers registered. External secret references will not be supported.");

    private static readonly Action<ILogger, int, string, Exception?> _logResolverInitialized =
        LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(4, nameof(_logResolverInitialized)),
            "Secret resolver initialized with {ProviderCount} providers: {Providers}");

    private static readonly Action<ILogger, string, Exception?> _logResolvingSecret =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, nameof(_logResolvingSecret)),
            "Resolving secret reference with provider '{Provider}'");

    private static readonly Action<ILogger, string, Exception?> _logSecretResolved =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(6, nameof(_logSecretResolved)),
            "Successfully resolved secret reference with provider '{Provider}'");

    private static readonly Action<ILogger, string, string, Exception?> _logResolveFailure =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(7, nameof(_logResolveFailure)),
            "Failed to resolve secret reference '{SecretRef}' with provider '{Provider}'");

    private static readonly Action<ILogger, string, Exception?> _logCanResolveError =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(8, nameof(_logCanResolveError)),
            "Error checking if secret reference can be resolved: {SecretRef}");

    public CompositeSecretResolver(
        IEnumerable<IConnectionSecretResolver> resolvers,
        ILogger<CompositeSecretResolver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Build provider map from resolvers
        _resolvers = new Dictionary<string, IConnectionSecretResolver>(StringComparer.OrdinalIgnoreCase);

        foreach (var resolver in resolvers ?? Enumerable.Empty<IConnectionSecretResolver>())
        {
            foreach (var provider in resolver.GetSupportedProviders())
            {
                if (_resolvers.ContainsKey(provider))
                {
                    _logDuplicateResolver(_logger, provider, null);
                    continue;
                }

                _resolvers[provider] = resolver;
                _logResolverRegistered(_logger, provider, resolver.GetType().Name, null);
            }
        }

        if (_resolvers.Count == 0)
        {
            _logNoResolversRegistered(_logger, null);
        }
        else
        {
            _logResolverInitialized(_logger, _resolvers.Count, string.Join(", ", _resolvers.Keys), null);
        }
    }

    public async Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new ArgumentException("Secret reference cannot be null or empty", nameof(secretRef));

        var (provider, path) = ParseSecretReference(secretRef);

        if (!_resolvers.TryGetValue(provider, out var resolver))
        {
            var supportedProviders = string.Join(", ", _resolvers.Keys);
            throw new InvalidOperationException(
                $"No secret resolver found for provider '{provider}'. " +
                $"Supported providers: {supportedProviders}");
        }

        try
        {
            _logResolvingSecret(_logger, provider, null);
            var connectionString = await resolver.ResolveConnectionStringAsync(secretRef, cancellationToken);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Secret reference '{secretRef}' resolved to null or empty value");
            }

            _logSecretResolved(_logger, provider, null);
            return connectionString;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logResolveFailure(_logger, secretRef, provider, ex);
            throw;
        }
    }

    public async Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(secretRef))
                return false;

            var (provider, _) = ParseSecretReference(secretRef);

            if (!_resolvers.TryGetValue(provider, out var resolver))
                return false;

            return await resolver.CanResolveSecretAsync(secretRef, cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logCanResolveError(_logger, secretRef, ex);
            return false;
        }
    }

    public string[] GetSupportedProviders()
    {
        return _resolvers.Keys.ToArray();
    }

    /// <summary>
    /// Parses a secret reference into provider and path components.
    /// </summary>
    /// <param name="secretRef">Secret reference to parse</param>
    /// <returns>Tuple of (provider, path)</returns>
    /// <exception cref="ArgumentException">Thrown when secret reference format is invalid</exception>
    private static (string Provider, string Path) ParseSecretReference(string secretRef)
    {
        var colonIndex = secretRef.IndexOf(':');
        if (colonIndex <= 0 || colonIndex == secretRef.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid secret reference format. Expected 'provider:path', got '{secretRef}'",
                nameof(secretRef));
        }

        var provider = secretRef[..colonIndex];
        var path = secretRef[(colonIndex + 1)..];

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                $"Invalid secret reference format. Provider and path cannot be empty. Got '{secretRef}'",
                nameof(secretRef));
        }

        return (provider, path);
    }
}

/// <summary>
/// Factory implementation for creating secret resolver instances.
/// </summary>
internal sealed class ConnectionSecretResolverFactory : IConnectionSecretResolverFactory
{
    private readonly Dictionary<string, Func<IConnectionSecretResolver>> _resolverFactories;
    private readonly ILogger<ConnectionSecretResolverFactory> _logger;

    // Logger message delegates for performance
    private static readonly Action<ILogger, string, string, Exception?> _logResolverCreated =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(1, nameof(_logResolverCreated)),
            "Created secret resolver for provider type '{ProviderType}': {ResolverType}");

    private static readonly Action<ILogger, string, Exception?> _logResolverCreationFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, nameof(_logResolverCreationFailure)),
            "Failed to create secret resolver for provider type '{ProviderType}'");

    private static readonly Action<ILogger, string, Exception?> _logFactoryAlreadyRegistered =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(_logFactoryAlreadyRegistered)),
            "Secret resolver factory for provider type '{ProviderType}' is already registered");

    private static readonly Action<ILogger, string, Exception?> _logFactoryRegistered =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4, nameof(_logFactoryRegistered)),
            "Registered secret resolver factory for provider type '{ProviderType}'");

    public ConnectionSecretResolverFactory(ILogger<ConnectionSecretResolverFactory> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _resolverFactories = new Dictionary<string, Func<IConnectionSecretResolver>>(StringComparer.OrdinalIgnoreCase)
        {
            ["env"] = () => new EnvironmentSecretResolver(),
            ["null"] = () => new NullSecretResolver()
        };
    }

    public IConnectionSecretResolver CreateResolver(string providerType)
    {
        if (string.IsNullOrWhiteSpace(providerType))
            throw new ArgumentException("Provider type cannot be null or empty", nameof(providerType));

        if (!_resolverFactories.TryGetValue(providerType, out var factory))
        {
            var supportedTypes = string.Join(", ", _resolverFactories.Keys);
            throw new ArgumentException(
                $"Unsupported provider type '{providerType}'. Supported types: {supportedTypes}",
                nameof(providerType));
        }

        try
        {
            var resolver = factory();
            _logResolverCreated(_logger, providerType, resolver.GetType().Name, null);
            return resolver;
        }
        catch (Exception ex)
        {
            _logResolverCreationFailure(_logger, providerType, ex);
            throw;
        }
    }

    public string[] GetAllSupportedProviders()
    {
        return _resolverFactories.Keys.ToArray();
    }

    /// <summary>
    /// Registers a new resolver factory for a provider type.
    /// </summary>
    /// <param name="providerType">Provider type identifier</param>
    /// <param name="factory">Factory function to create resolver instances</param>
    /// <returns>True if registered, false if provider type already exists</returns>
    public bool RegisterResolverFactory(string providerType, Func<IConnectionSecretResolver> factory)
    {
        if (string.IsNullOrWhiteSpace(providerType))
            throw new ArgumentException("Provider type cannot be null or empty", nameof(providerType));

        ArgumentNullException.ThrowIfNull(factory);

        if (_resolverFactories.ContainsKey(providerType))
        {
            _logFactoryAlreadyRegistered(_logger, providerType, null);
            return false;
        }

        _resolverFactories[providerType] = factory;
        _logFactoryRegistered(_logger, providerType, null);
        return true;
    }
}
