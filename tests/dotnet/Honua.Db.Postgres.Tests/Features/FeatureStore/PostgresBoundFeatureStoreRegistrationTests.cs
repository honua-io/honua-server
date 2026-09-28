// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Caching;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresBoundFeatureStoreRegistrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _sourceString = null!;

    public async Task InitializeAsync()
    {
        _sourceString = await fixture.CreateIsolatedDatabaseAsync(nameof(PostgresBoundFeatureStoreRegistrationTests));
        await using var connection = new NpgsqlConnection(_sourceString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TABLE public.registration_pool_probe (id bigint PRIMARY KEY, name text);
            INSERT INTO public.registration_pool_probe VALUES (17, 'registered source database');
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => fixture.DropDatabaseAsync(new NpgsqlConnectionStringBuilder(_sourceString).Database!);

    [IntegrationTest]
    public async Task RegisteredFeatureStore_BoundReadSharesPrimaryAdmission_AndUsesSourceDatabase()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString,
            ["Limits:Connections:MaxConnectionPoolSize"] = "1",
            ["Limits:Connections:MinConnectionPoolSize"] = "0",
            ["Limits:Connections:MaxConcurrentQueries"] = "1",
            ["Limits:Connections:ConnectionAcquisitionTimeoutSeconds"] = "10"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        Honua.Db.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(
            services, configuration, TestCoreSchemaMigrations.Manifest);

        // Keep the real feature-store factory, primary provider, bound provider,
        // data-source cache, and gate. Catalog/security collaborators are unrelated
        // to the connection ownership being exercised and need no server bootstrap.
        services.AddSingleton(Substitute.For<IFeatureQueryBuilder>());
        services.AddSingleton(Substitute.For<IFeatureDataAccess>());
        services.AddSingleton(Substitute.For<IFeatureCacheManager>());
        services.AddSingleton(Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(Substitute.For<IConnectionEncryptionService>());
        services.AddSingleton(Substitute.For<ISecureConnectionResolver>());
        services.AddSingleton(Substitute.For<IConnectionSecretResolver>());

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        var featureStore = scoped.GetRequiredService<IFeatureDataProvider>();
        featureStore.Should().BeOfType<PostgresFeatureStoreRefactored>();
        var primary = scoped.GetRequiredService<IPrimaryDatabaseConnectionProvider>();
        primary.Should().BeSameAs(scoped.GetRequiredService<CachingDatabaseConnectionProvider>());
        primary.Should().BeSameAs(scoped.GetRequiredService<IDatabaseConnectionProvider>());
        var gate = scoped.GetRequiredService<QueryConcurrencyGate>();
        gate.MaxLimit.Should().Be(1);

        // GeoBench publishes a discovered table through a managed connection;
        // this is the resulting runtime binding passed to the registered store.
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "registered-source", Name = "registered-source" },
            SchemaFields = [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }]
        };
        var binding = new FeatureProviderBinding(
            new MetadataV2Service(), resource, new MetadataV2Publication(),
            new MetadataV2StorageBinding { ConnectionId = "registered-source", ResourceId = resource.Metadata.Id },
            new FeatureStorageMapping("registration_pool_probe", SchemaName: "public", PrimaryKeyColumn: "id"),
            1, featureStore,
            new DataConnection { Id = "registered-source", IsEncrypted = false, ConnectionString = _sourceString });
        var reader = (IPagedFeatureReader)((IBindableFeatureDataProvider)featureStore).CreateReaderForBinding(binding);

        await using var blocker = await primary.OpenConnectionAsync();
        blocker.Database.Should().Be(new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database);
        blocker.Database.Should().NotBe(new NpgsqlConnectionStringBuilder(_sourceString).Database);
        var pending = reader.QueryPageAsync(1, new FeatureQuery { Limit = 1 });

        gate.AvailableSlots.Should().Be(0);
        gate.GetSnapshot().QueuedWaiters.Should().Be(1,
            "the DI-created bound reader must use the primary admission gate");
        pending.IsCompleted.Should().BeFalse();

        await blocker.DisposeAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        var feature = result.Items.Should().ContainSingle().Which;
        feature.Id.Should().Be(17);
        feature.Attributes["name"].Should().Be("registered source database");
        gate.AvailableSlots.Should().Be(1);
        gate.GetSnapshot().QueuedWaiters.Should().Be(0);
    }
}
