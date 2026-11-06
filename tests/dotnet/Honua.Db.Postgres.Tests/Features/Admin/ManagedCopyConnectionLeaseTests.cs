// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Reflection;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[Collection("Database")]
public sealed class ManagedCopyConnectionLeaseTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateManagedCopyTarget_WithWrappedConnection_PreservesStoreIdentityAndReleasesLease(bool sameStore)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("managed_copy_lease");
        try
        {
            await using (var setup = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = setup.CreateCommand())
            {
                command.CommandText = $"CREATE TABLE IF NOT EXISTS \"{schema}\".features (id integer);";
                await command.ExecuteNonQueryAsync();
            }

            var sourceConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = $"{schema},public"
            }.ConnectionString;
            var releaseCount = 0;
            var provider = new Mock<IAdoNetDatabaseConnectionProvider>();
            provider.Setup(value => value.OpenConnectionAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken cancellationToken) =>
                {
                    var inner = new NpgsqlConnection(sourceConnectionString);
                    await inner.OpenAsync(cancellationToken);
                    return (DbConnection)new SemaphoreReleasingConnection(inner, () => releaseCount++);
                });
            var service = new PostgreSqlLayerPublishingService(
                Mock.Of<ITableDiscoveryService>(),
                Mock.Of<IMetadataV2GraphStore>(),
                NullLogger<PostgreSqlLayerPublishingService>.Instance,
                metadataSchema: sameStore ? schema : "public",
                featureStoreConnections: provider.Object);
            var readTarget = typeof(PostgreSqlLayerPublishingService).GetMethod(
                "ReadManagedCopyTargetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var validateTarget = typeof(PostgreSqlLayerPublishingService).GetMethod(
                "ValidateManagedCopyTargetAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var fieldType = typeof(PostgreSqlLayerPublishingService).GetNestedType(
                "LayerFieldInsert", BindingFlags.NonPublic)!;
            var fields = Array.CreateInstance(fieldType, 0);
            // The managed lease is taken and released while the target identity is captured.
            // Validation then compares that identity with the caller's already-open source.
            async Task<string> ValidateAsync()
            {
                var pending = (Task)readTarget.Invoke(service, [CancellationToken.None])!;
                await pending.ConfigureAwait(false);
                var target = pending.GetType().GetProperty("Result")!.GetValue(pending);
                await using var source = new NpgsqlConnection(sourceConnectionString);
                await source.OpenAsync().ConfigureAwait(false);
                return await ((Task<string>)validateTarget.Invoke(
                    null, [source, fields, target, CancellationToken.None])!).ConfigureAwait(false);
            }

            if (sameStore)
            {
                (await ValidateAsync()).Should().Be(schema);
            }
            else
            {
                await FluentActions.Awaiting(ValidateAsync).Should().ThrowAsync<LayerPublishingException>();
            }

            releaseCount.Should().Be(1, "the provider concurrency slot must be released on success and rejection");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
