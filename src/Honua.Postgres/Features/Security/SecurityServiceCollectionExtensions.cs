// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
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

        // Register secret resolvers
        services.AddAwsSecretsManagerSupport(configuration);
        services.AddAzureKeyVaultSupport(configuration);
        services.AddGcpSecretManagerSupport(configuration);

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
                serviceProvider.GetRequiredService<GcpSecretManagerResolver>(),
                serviceProvider.GetRequiredService<NullSecretResolver>() // Fallback
            };
            return new CompositeSecretResolver(resolvers, logger);
        });

        // Register secret resolver factory for extensibility
        services.AddSingleton<IConnectionSecretResolverFactory>(serviceProvider =>
            new ConnectionSecretResolverFactory(
                serviceProvider.GetRequiredService<ILogger<ConnectionSecretResolverFactory>>()));

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
        // Replace the existing IDatabaseConnectionProvider registration
        // Capture the current provider descriptor so we can wrap it safely.
        if (services.LastOrDefault(s => s.ServiceType == typeof(IDatabaseConnectionProvider)) is not { } existingDescriptor)
        {
            throw new InvalidOperationException("No default database connection provider found");
        }

        services.Remove(existingDescriptor);

        // Register the original provider under a stable service type to avoid circular dependencies.
        services.Add(new ServiceDescriptor(
            typeof(IPrimaryDatabaseConnectionProvider),
            serviceProvider =>
            {
                var provider = ResolveOriginalProvider(existingDescriptor, serviceProvider);
                return provider is IPrimaryDatabaseConnectionProvider primary
                    ? primary
                    : new PrimaryDatabaseConnectionProviderAdapter(provider);
            },
            existingDescriptor.Lifetime));

        // Register the secure connection-aware provider as a decorator
        services.Add(new ServiceDescriptor(
            typeof(IDatabaseConnectionProvider),
            serviceProvider =>
            {
                var originalProvider = serviceProvider.GetRequiredService<IPrimaryDatabaseConnectionProvider>();
                var secureResolver = serviceProvider.GetRequiredService<ISecureConnectionResolver>();
                var logger = serviceProvider.GetRequiredService<ILogger<SecureConnectionAwareDatabaseProvider>>();
                var schemaContext = serviceProvider.GetService<ISchemaContext>();
                var activeDbConnectionTracker = serviceProvider.GetService<IActiveDbConnectionTracker>();

                return new SecureConnectionAwareDatabaseProvider(
                    originalProvider,
                    secureResolver,
                    configuration,
                    logger,
                    schemaContext,
                    activeDbConnectionTracker);
            },
            existingDescriptor.Lifetime));

        return services;
    }

    private static IDatabaseConnectionProvider ResolveOriginalProvider(
        ServiceDescriptor descriptor,
        IServiceProvider serviceProvider)
    {
        if (descriptor.ImplementationInstance is IDatabaseConnectionProvider instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IDatabaseConnectionProvider)descriptor.ImplementationFactory(serviceProvider);
        }

        if (descriptor.ImplementationType is not null)
        {
            return (IDatabaseConnectionProvider)ActivatorUtilities.CreateInstance(
                serviceProvider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve default database connection provider");
    }

    private sealed class PrimaryDatabaseConnectionProviderAdapter : IPrimaryDatabaseConnectionProvider
    {
        private readonly IDatabaseConnectionProvider _inner;

        public PrimaryDatabaseConnectionProviderAdapter(IDatabaseConnectionProvider inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

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
        services.AddHttpClient("AwsSecretsManager", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient("AwsMetadata", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(2);
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
        services.AddHttpClient("AzureKeyVault", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.TryAddSingleton<AzureKeyVaultResolver>();
        return services;
    }

    /// <summary>
    /// Adds Google Secret Manager secret resolver support.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <param name="configuration">Application configuration</param>
    /// <returns>Updated service collection for chaining</returns>
    /// <remarks>
    /// Registers the AOT-safe GCP Secret Manager resolver using HTTP + service account JWT or metadata tokens.
    /// Credentials are sourced from GOOGLE_APPLICATION_CREDENTIALS or GCE metadata server.
    /// </remarks>
    public static IServiceCollection AddGcpSecretManagerSupport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpClient("GcpSecretManager", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.TryAddSingleton<GcpSecretManagerResolver>();
        return services;
    }

    /// <summary>
    /// Validates that secure connection services are properly configured.
    /// </summary>
    /// <param name="serviceProvider">Service provider to validate</param>
    /// <returns>True if configuration is valid, false otherwise</returns>
    /// <remarks>
    /// This method can be called during application startup to verify
    /// that all security components are properly configured and functional.
    /// </remarks>
    public static async Task<bool> ValidateSecureConnectionConfiguration(IServiceProvider serviceProvider)
    {
        try
        {
            // Test encryption service
            var encryptionService = serviceProvider.GetRequiredService<IConnectionEncryptionService>();
            if (!await encryptionService.ValidateEncryptionAsync())
            {
                return false;
            }

            // Test secret resolver registration
            var secretResolver = serviceProvider.GetRequiredService<IConnectionSecretResolver>();
            var supportedProviders = secretResolver.GetSupportedProviders();
            if (supportedProviders.Length == 0)
            {
                return false;
            }

            // Test registry access (basic connection test)
            var registry = serviceProvider.GetRequiredService<ISecureConnectionRegistry>();
            _ = await registry.GetActiveConnectionsAsync();

            return true;
        }
        catch
        {
            return false;
        }
    }
}
