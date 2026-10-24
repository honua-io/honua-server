// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Db.Postgres.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class ServiceCollectionExtensionsWorkspaceTests
{
    [Fact]
    public void AddPostgreSqlServices_RegistersWorkspaceAndArtifactStorageForGeoprocessing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Database=honua_test;Username=honua"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IWorkspaceStore),
            "the advertised env:workspace workflow needs a production workspace provider");
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IArtifactStore),
            "the advertised overwriteOutput workflow needs durable artifact storage");
    }
}
