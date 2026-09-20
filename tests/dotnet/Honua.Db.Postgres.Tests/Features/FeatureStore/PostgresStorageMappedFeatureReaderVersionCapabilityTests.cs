// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>Binding capability discovery uses the same eligibility as branch reads (#5047).</summary>
public sealed class PostgresStorageMappedFeatureReaderVersionCapabilityTests
{
    private const string ManagedConnection = "Host=localhost;Port=5432;Database=managed;Username=capability_test";
    private static readonly FeatureStorageMapping ManagedMapping = new("features",
        PrimaryKeyColumn: "objectid", GeometryColumn: "geometry", AttributesColumn: "attributes",
        LayerDiscriminatorColumn: "layer_id", LayerDiscriminatorValue: 7);

    [Theory]
    [InlineData("managed", true)]
    [InlineData("source-managed", true)]
    [InlineData("table", false)]
    [InlineData("source-table", false)]
    [InlineData("primary-key", false)]
    [InlineData("attributes", false)]
    [InlineData("discriminator", false)]
    [InlineData("missing-layer", false)]
    [InlineData("geometry", false)]
    [InlineData("nonspatial", true)]
    public async Task MappingCapability_AgreesWithBranchReadGuard(string shape, bool expected)
    {
        var sourceOptions = new Dictionary<string, string> { [FeatureStorageMapping.SourceBackedOption] = "true" };
        var mapping = shape switch
        {
            "source-managed" => ManagedMapping with { ProviderOptions = sourceOptions },
            "table" => ManagedMapping with { TableName = "external_features" },
            "source-table" => ManagedMapping with { TableName = "external_features", ProviderOptions = sourceOptions },
            "primary-key" => ManagedMapping with { PrimaryKeyColumn = "fid" },
            "attributes" => ManagedMapping with { AttributesColumn = "properties" },
            "discriminator" => ManagedMapping with { LayerDiscriminatorColumn = "collection_id" },
            "missing-layer" => ManagedMapping with { LayerDiscriminatorValue = null },
            "geometry" => ManagedMapping with { GeometryColumn = "shape" },
            "nonspatial" => ManagedMapping with { GeometryColumn = null },
            _ => ManagedMapping
        };
        var (reader, connectionProvider) = CreateReader(mapping);
        (await reader.SupportsBranchVersioningAsync()).Should().Be(expected);
        if (!expected)
        {
            var read = () => reader.QueryAsync(7, new FeatureQuery
            {
                VersionContext = new VersionContext { VersionId = Guid.NewGuid() }
            });
            await read.Should().ThrowAsync<NotSupportedException>();
        }
        AssertNoDatabaseOpened(connectionProvider);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("placeholder", true)]
    [InlineData("empty-id-stale", true)]
    [InlineData("equivalent-plain", true)]
    [InlineData("external-plain", false)]
    [InlineData("equivalent-encrypted", true)]
    [InlineData("external-encrypted", false)]
    [InlineData("unresolved-encrypted", false)]
    [InlineData("secret-reference", false)]
    [InlineData("malformed", false)]
    [InlineData("decrypt-failed", false)]
    public async Task ConnectionCapability_UsesCanonicalResolutionWithoutOpeningDatabase(string binding, bool expected)
    {
        const string external = "Host=localhost;Port=5432;Database=external;Username=capability_test";
        var connection = binding switch
        {
            "none" => null,
            "placeholder" => new DataConnection { Id = "managed-placeholder", IsEncrypted = false },
            "empty-id-stale" => new DataConnection { Id = "", IsEncrypted = true, EncryptedConnectionString = [1] },
            "equivalent-plain" => new DataConnection
            {
                Id = "explicit-managed",
                IsEncrypted = false,
                ConnectionString = "Username=capability_test;Database=managed;Port=5432;Host=localhost"
            },
            "external-plain" => new DataConnection { Id = "external", IsEncrypted = false, ConnectionString = external },
            "secret-reference" => new DataConnection { Id = "external-secret", SecretRef = "test://unresolved", IsEncrypted = false },
            "malformed" => new DataConnection { Id = "malformed", ConnectionString = "not a connection string", IsEncrypted = false },
            _ => new DataConnection { Id = "encrypted", IsEncrypted = true, EncryptedConnectionString = [1], EncryptionKeyVersion = 2 }
        };
        var encryption = Substitute.For<IConnectionEncryptionService>();
        encryption.DecryptConnectionStringAsync(Arg.Any<byte[]>(), Arg.Any<int>()).Returns(
            binding switch
            {
                "equivalent-encrypted" => Task.FromResult(ManagedConnection),
                "external-encrypted" => Task.FromResult(external),
                "decrypt-failed" => Task.FromException<string>(new CryptographicException("Test decryption failed")),
                _ => Task.FromResult(string.Empty)
            });
        var (reader, connectionProvider) = CreateReader(ManagedMapping, connection, encryption);
        (await reader.SupportsBranchVersioningAsync()).Should().Be(expected);
        AssertNoDatabaseOpened(connectionProvider);
    }

    [Fact]
    public async Task CapabilityResolution_PreservesCancellation()
    {
        var (reader, _) = CreateReader(ManagedMapping);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var resolve = () => reader.SupportsBranchVersioningAsync(cancellation.Token);
        await resolve.Should().ThrowAsync<OperationCanceledException>();
    }

    private static (PostgresStorageMappedFeatureReader Reader, IAdoNetDatabaseConnectionProvider ConnectionProvider) CreateReader(
        FeatureStorageMapping mapping, DataConnection? connection = null, IConnectionEncryptionService? encryption = null)
    {
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.GetConnectionString().Returns(ManagedConnection);
        var pool = new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());
        return (new PostgresStorageMappedFeatureReader(provider, pool, new MetadataV2Resource(), mapping, connection, encryption), provider);
    }

    private static void AssertNoDatabaseOpened(IAdoNetDatabaseConnectionProvider provider)
        => provider.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name != "GetConnectionString",
            "capability discovery resolves connection identity but never opens a connection or queries feature rows");
}
