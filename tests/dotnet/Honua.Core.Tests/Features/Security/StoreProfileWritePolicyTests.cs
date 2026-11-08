// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Security;

public sealed class StoreProfileWritePolicyTests
{
    private static bool AdditionalStoresExpected =>
        Environment.GetEnvironmentVariable("HONUA_EXPECTED_STORE_PROFILE") != "postgis";

    [UnitTest]
    public void CompiledAvailability_MatchesRequestedProfile()
    {
        DataStoreProviderPolicy.AdditionalStoresIncluded.Should().Be(AdditionalStoresExpected);
    }

    [UnitTheory]
    [InlineData(DataProviderNames.MySql)]
    [InlineData(DataProviderNames.SqlServer)]
    [InlineData(DataProviderNames.GeoServicesRest)]
    public void Validate_AdditionalStore_EnforcesCompiledProfile(string provider)
    {
        var connection = new DataConnection
        {
            Name = "fixture",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "fixture",
            Username = "fixture",
            Provider = provider
        };
        Action validate = () => connection.Validate();

        if (AdditionalStoresExpected)
            validate.Should().NotThrow();
        else
            validate.Should().Throw<InvalidOperationException>().WithMessage("*-full*");
    }

    [UnitTheory]
    [InlineData(DataProviderNames.MySql)]
    [InlineData(DataProviderNames.SqlServer)]
    [InlineData(DataProviderNames.GeoServicesRest)]
    public async Task CreateConnectionAsync_AdditionalStore_EnforcesCompiledProfile(string provider)
    {
        var registry = new InMemorySecureConnectionRegistry();
        var connection = new DataConnection { Name = "fixture", Provider = provider };

        await AssertWritePolicyAsync(() => registry.CreateConnectionAsync(connection));
        if (!AdditionalStoresExpected)
            (await registry.GetAllConnectionsAsync()).Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData(DataProviderNames.MySql)]
    [InlineData(DataProviderNames.SqlServer)]
    [InlineData(DataProviderNames.GeoServicesRest)]
    public async Task UpdateConnectionAsync_AdditionalStore_EnforcesCompiledProfile(string provider)
    {
        var registry = new InMemorySecureConnectionRegistry();
        var connection = new DataConnection { Name = "fixture", Provider = provider };

        await AssertWritePolicyAsync(() => registry.UpdateConnectionAsync(connection));
        if (!AdditionalStoresExpected)
            (await registry.GetAllConnectionsAsync()).Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData(DataProviderNames.MySql)]
    [InlineData(DataProviderNames.SqlServer)]
    [InlineData(DataProviderNames.GeoServicesRest)]
    public void ValidateGraph_AdditionalStore_EnforcesCompiledProfile(string provider)
    {
        var graph = new MetadataV2Graph
        {
            Connections = [new MetadataV2Connection {
            Metadata = new MetadataV2ObjectMetadata { Id = "fixture", Name = "fixture" },
            Type = MetadataV2ConnectionType.Database, Provider = provider }]
        };

        var result = MetadataV2GraphValidator.Validate(graph);

        result.IsValid.Should().Be(AdditionalStoresExpected);
        if (!AdditionalStoresExpected)
            result.Errors.Should().Contain(error => error.Contains("-full", StringComparison.Ordinal));
    }

    private static async Task AssertWritePolicyAsync(Func<Task> write)
    {
        if (AdditionalStoresExpected)
            await write.Should().NotThrowAsync();
        else
            await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*-full*");
    }

    [UnitTheory]
    [InlineData(MetadataV2ConnectionType.FileSystem, "geopackage")]
    [InlineData(MetadataV2ConnectionType.FileSystem, "shapefile")]
    [InlineData(MetadataV2ConnectionType.FileSystem, "geotiff")]
    [InlineData(MetadataV2ConnectionType.FileSystem, "parquet")]
    [InlineData(MetadataV2ConnectionType.ObjectStorage, null)]
    [InlineData(MetadataV2ConnectionType.Stac, null)]
    [InlineData(MetadataV2ConnectionType.HttpApi, null)]
    [InlineData(MetadataV2ConnectionType.Database, DataProviderNames.Postgis)]
    [InlineData(MetadataV2ConnectionType.Managed, DataProviderNames.PostgreSql)]
    public void ValidateGraph_IncludedStoresAndFormatSources_Accepts(MetadataV2ConnectionType type, string? provider)
    {
        var graph = new MetadataV2Graph
        {
            Connections = [new MetadataV2Connection {
            Metadata = new MetadataV2ObjectMetadata { Id = "fixture", Name = "fixture" },
            Type = type, Provider = provider }]
        };

        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
    }
}
