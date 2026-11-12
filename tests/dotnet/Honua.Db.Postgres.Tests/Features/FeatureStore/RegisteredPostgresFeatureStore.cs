// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

internal static class RegisteredPostgresFeatureStore
{
    public static async Task<long> CountThroughBoundReaderAsync(
        IReadOnlyDictionary<string, string?> settings,
        IAdoNetDatabaseConnectionProvider connectionProvider,
        Func<IFeatureDataProvider, FeatureProviderBinding> bind,
        FeatureQuery query)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        Honua.Db.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(
            services, configuration, TestCoreSchemaMigrations.Manifest);
        services.AddSingleton(Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(Substitute.For<IConnectionEncryptionService>());
        services.AddSingleton(Substitute.For<ISecureConnectionResolver>());
        services.AddSingleton(Substitute.For<IConnectionSecretResolver>());
        services.AddSingleton(connectionProvider);
        await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = container.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IFeatureDataProvider>();
        var reader = ((IBindableFeatureDataProvider)store).CreateReaderForBinding(bind(store));
        return await reader.CountAsync(1, query);
    }
}
