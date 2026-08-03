// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Postgres.Features.Raster;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgresRasterOutputRegistryTests
{
    [Fact]
    public void ObjectLeaseSql_UsesSharedLocksForReadersAndExclusiveLocksForMutators()
    {
        Assert.Contains("pg_advisory_lock_shared", PostgresRasterOutputRegistry.BuildAcquireLeaseSql(shared: true), StringComparison.Ordinal);
        Assert.Contains("pg_advisory_unlock_shared", PostgresRasterOutputRegistry.BuildReleaseLeaseSql(shared: true), StringComparison.Ordinal);
        Assert.Contains("pg_advisory_lock(", PostgresRasterOutputRegistry.BuildAcquireLeaseSql(shared: false), StringComparison.Ordinal);
        Assert.Contains("pg_advisory_unlock(", PostgresRasterOutputRegistry.BuildReleaseLeaseSql(shared: false), StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogRegistration_RejectsUnsupportedLocalStore()
    {
        var action = () => PostgresRasterOutputRegistry.EnsureCatalogProviderSupported(
            CloudStorageProvider.Local);

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("AWS S3 or Azure Blob", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CloudStorageProvider.AwsS3)]
    [InlineData(CloudStorageProvider.AzureBlob)]
    public void CatalogRegistration_AcceptsCloudRangeReaderProviders(CloudStorageProvider provider)
    {
        var exception = Record.Exception(() =>
            PostgresRasterOutputRegistry.EnsureCatalogProviderSupported(provider));

        Assert.Null(exception);
    }
}
