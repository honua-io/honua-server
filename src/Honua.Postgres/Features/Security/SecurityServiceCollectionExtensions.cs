// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Security.ConnectionSecretResolvers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Security;

/// <summary>
/// Service collection extensions for secure connection management.
/// </summary>
internal static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Adds secure connection management services to the service collection.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <param name="configuration">Application configuration</param>
    /// <returns>Updated service collection for chaining</returns>
    /// <remarks>
    /// Registers:
    /// - Encryption service for connection string encryption/decryption
    /// - Secret resolvers for external secret management integration
    /// - Secure connection registry for CRUD operations
    /// - Secure connection resolver for runtime connection resolution
    ///
    /// All services are registered with appropriate lifetimes and logging.
    /// </remarks>
    public static IServiceCollection AddSecureConnectionServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register encryption service as singleton for performance
        // The service maintains key state and can be safely shared across requests
        services.AddSingleton<IConnectionEncryptionService>(serviceProvider =>
            new ConnectionEncryptionService(
                configuration,
                serviceProvider.GetRequiredService<ILogger<ConnectionEncryptionService>>()));

        services.TryAddSingleton<IDatabaseConnectionStringBuilder, PostgresConnectionStringBuilder>();
        services.TryAddSingleton<IConnectionHealthTester, PostgresConnectionHealthTester>();

        // Outbound data-source connection host policy (#354): an operator-configured
        // allowlist of permitted database destinations plus optional private-address
        // blocking, generalizing the outbound-HTTP SSRF guard (#2004). Opt-in — an
        // unconfigured section leaves registration unrestricted.
        services.TryAddSingleton<IConnectionHostAllowlist>(_ =>
            new ConnectionHostAllowlist(BindConnectionHostAllowlistOptions(configuration)));

        // Register secret resolvers
        services.AddAwsSecretsManagerSupport(configuration);
        services.AddAzureKeyVaultSupport(configuration);

        services.TryAddSingleton<EnvironmentSecretResolver>();
        services.TryAddSingleton<NullSecretResolver>();

        services.AddSingleton<IConnectionSecretResolver>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<CompositeSecretResolver>>();
            var resolvers = new IConnectionSecretResolver[]
            {
                serviceProvider.GetRequiredService<EnvironmentSecretResolver>(),
                serviceProvider.GetRequiredService<AwsSecretsManagerResolver>(),
                serviceProvider.GetRequiredService<AzureKeyVaultResolver>(),
                serviceProvider.GetRequiredService<NullSecretResolver>() // Fallback
            };
            return new CompositeSecretResolver(resolvers, logger);
        });

        // Register secret resolver factory for extensibility
        services.AddSingleton<IConnectionSecretResolverFactory>(serviceProvider =>
            new ConnectionSecretResolverFactory(
                serviceProvider.GetRequiredService<ILogger<ConnectionSecretResolverFactory>>()));

        services.AddSingleton<SecureConnectionDataSourceCache>();

        // Register secure connection registry as scoped (database operations)
        services.AddScoped<ISecureConnectionRegistry, PostgresSecureConnectionRegistry>();

        // Register secure connection resolver as scoped (combines registry + encryption + secrets)
        services.AddScoped<ISecureConnectionResolver, SecureConnectionResolver>();

        return services;
    }

    /// <summary>
    /// Replaces the default database connection provider with a secure connection-aware version.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <param name="configuration">Application configuration</param>
    /// <returns>Updated service collection for chaining</returns>
    /// <remarks>
    /// This method should be called after AddSecureConnectionServices if you want to enable
    /// automatic secure connection resolution for all database operations.
    ///
    /// When enabled, the system will:
    /// 1. Check if Database:SecureConnection:Name is configured
    /// 2. If yes, resolve connections from the secure registry
    /// 3. If no or resolution fails, fall back to DefaultConnection
    ///
    /// This maintains backward compatibility while enabling secure connection features.
    /// </remarks>
    public static IServiceCollection UseSecureConnectionProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Replace the existing IAdoNetDatabaseConnectionProvider registration
        // Capture the current provider descriptor so we can wrap it safely.
        if (services.LastOrDefault(s => s.ServiceType == typeof(IAdoNetDatabaseConnectionProvider)) is not { } existingDescriptor)
        {
            throw new InvalidOperationException("No default database connection provider found");
        }

        services.Remove(existingDescriptor);

        // Register the original provider under a stable service type to avoid circular dependencies.
        // The base descriptor may resolve to nothing when the database layer is intentionally
        // unwired (e.g. DB-less / mocked test hosts that strip IDatabaseConnectionProvider and
        // NpgsqlDataSource). In that case the primary provider degrades to null so the secure
        // decorator below can also degrade to null and callers using GetService(...) see the
        // "no database wired" contract instead of a hard resolution failure.
        services.Add(new ServiceDescriptor(
            typeof(IPrimaryDatabaseConnectionProvider),
            serviceProvider =>
            {
                var provider = ResolveOriginalProvider(existingDescriptor, serviceProvider);
                if (provider is null)
                {
                    return null!;
                }

                return provider is IPrimaryDatabaseConnectionProvider primary
                    ? primary
                    : new PrimaryDatabaseConnectionProviderAdapter(provider);
            },
            existingDescriptor.Lifetime));

        // Register the secure connection-aware provider as a decorator
        services.Add(new ServiceDescriptor(
            typeof(IAdoNetDatabaseConnectionProvider),
            serviceProvider =>
            {
                var originalProvider = serviceProvider.GetService<IPrimaryDatabaseConnectionProvider>();
                if (originalProvider is null)
                {
                    // No base provider — the database layer is unwired. Degrade to null so
                    // GetService<IAdoNetDatabaseConnectionProvider>() returns null rather than
                    // throwing while constructing the decorator.
                    return null!;
                }

                var secureResolver = serviceProvider.GetRequiredService<ISecureConnectionResolver>();
                var dataSourceCache = serviceProvider.GetRequiredService<SecureConnectionDataSourceCache>();
                var logger = serviceProvider.GetRequiredService<ILogger<SecureConnectionAwareDatabaseProvider>>();
                var schemaContext = serviceProvider.GetService<ISchemaContext>();
                var activeDbConnectionTracker = serviceProvider.GetService<IActiveDbConnectionTracker>();
                // Share the same singleton gate as CachingDatabaseConnectionProvider so
                // secure-mode opens honour MaxConcurrentQueries / acquisition timeouts.
                var concurrencyGate = serviceProvider.GetService<QueryConcurrencyGate>();

                return new SecureConnectionAwareDatabaseProvider(
                    originalProvider,
                    secureResolver,
                    dataSourceCache,
                    configuration,
                    logger,
                    schemaContext,
                    activeDbConnectionTracker,
                    concurrencyGate);
            },
            existingDescriptor.Lifetime));

        return services;
    }

    private static IAdoNetDatabaseConnectionProvider? ResolveOriginalProvider(
        ServiceDescriptor descriptor,
        IServiceProvider serviceProvider)
    {
        if (descriptor.ImplementationInstance is IAdoNetDatabaseConnectionProvider instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            // The captured base descriptor (ADR-0046 escape hatch) is a forwarding factory
            // that resolves IDatabaseConnectionProvider. In DB-less / mocked test hosts that
            // service mapping is stripped, so the factory yields null. Treat a missing base
            // mapping as "no database wired" and degrade to null; when the base IS present
            // keep the original hard cast so the real DB path is unchanged.
            var resolved = descriptor.ImplementationFactory(serviceProvider);
            return resolved is null ? null : (IAdoNetDatabaseConnectionProvider)resolved;
        }

        if (descriptor.ImplementationType is not null)
        {
            return (IAdoNetDatabaseConnectionProvider)ActivatorUtilities.CreateInstance(
                serviceProvider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve default database connection provider");
    }

    private sealed class PrimaryDatabaseConnectionProviderAdapter : IPrimaryDatabaseConnectionProvider
    {
        private readonly IAdoNetDatabaseConnectionProvider _inner;

        public PrimaryDatabaseConnectionProviderAdapter(IAdoNetDatabaseConnectionProvider inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public string GetConnectionString()
            => _inner.GetConnectionString();

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => _inner.OpenConnectionAsync(cancellationToken);

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => _inner.OpenTransactionAsync(isolationLevel, cancellationToken);

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => _inner.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => _inner.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);
    }

    /// <summary>
    /// Adds AWS Secrets Manager secret resolver support.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <param name="configuration">Application configuration</param>
    /// <returns>Updated service collection for chaining</returns>
    /// <remarks>
    /// Registers the AOT-safe AWS Secrets Manager resolver using HTTP + SigV4 signing.
    /// Credentials are sourced from standard AWS environment variables or IMDS/ECS metadata.
    /// </remarks>
    public static IServiceCollection AddAwsSecretsManagerSupport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddResilientHttpClient(
            "AwsSecretsManager",
            "aws-secrets-manager",
            HttpResiliencePolicies.FastApiDefaults,
            configureClient: client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
            });

        services.AddResilientHttpClient(
            "AwsMetadata",
            "aws-metadata",
            HttpResiliencePolicies.FastApiDefaults,
            configureClient: client =>
            {
                client.Timeout = TimeSpan.FromSeconds(2);
            },
            configureHandler: static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });

        services.TryAddSingleton<AwsSecretsManagerResolver>();
        return services;
    }

    /// <summary>
    /// Adds Azure Key Vault secret resolver support.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <param name="configuration">Application configuration</param>
    /// <returns>Updated service collection for chaining</returns>
    /// <remarks>
    /// Registers the AOT-safe Azure Key Vault resolver using HTTP + OAuth token exchange.
    /// Credentials are sourced from AZURE_* client credentials or managed identity.
    /// </remarks>
    public static IServiceCollection AddAzureKeyVaultSupport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddResilientHttpClient(
            "AzureKeyVault",
            "azure-key-vault",
            HttpResiliencePolicies.FastApiDefaults,
            configureClient: client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
            });

        services.AddResilientHttpClient(
            "AzureManagedIdentityMetadata",
            "azure-managed-identity",
            HttpResiliencePolicies.FastApiDefaults,
            configureClient: client =>
            {
                client.Timeout = TimeSpan.FromSeconds(2);
            },
            configureHandler: static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });

        services.TryAddSingleton<AzureKeyVaultResolver>();
        return services;
    }

    /// <summary>
    /// Binds <see cref="ConnectionHostAllowlistOptions"/> from the
    /// <c>Security:ConnectionAllowlist</c> configuration section without relying on
    /// reflection-based binding, keeping the path AOT-safe.
    /// </summary>
    private static ConnectionHostAllowlistOptions BindConnectionHostAllowlistOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(ConnectionHostAllowlistOptions.SectionName);

        var allowedHosts = section.GetSection("AllowedHosts").GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();

        var blockPrivate = section.GetValue("BlockPrivateAddresses", false);

        return new ConnectionHostAllowlistOptions
        {
            AllowedHosts = allowedHosts,
            BlockPrivateAddresses = blockPrivate
        };
    }
}
