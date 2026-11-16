// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure;

public class PostgresFixtureMigrationDiscoveryTests
{
    [UnitTest]
    public void Discover_EmbeddedMarkers_SelectsCanonicalSqlInFixturePhaseOrder()
    {
        var migrations = PostgresFixtureMigrations.Discover();

        migrations.Should().OnlyHaveUniqueItems();
        migrations.Should().Contain("Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql");
        migrations.Should().Contain("Honua.Server.Migrations.167_CreateWorkflowStores.sql");
        var lastStore = Array.IndexOf(migrations, "Honua.Server.Migrations.167_CreateWorkflowStores.sql");
        var firstImportFunction = Array.IndexOf(migrations, "Honua.Server.Migrations.070_AddImportLoadModes.sql");
        var typedColumns = Array.IndexOf(migrations, "Honua.Server.Migrations.143_PromoteImportTypedColumns.sql");
        firstImportFunction.Should().BeGreaterThan(lastStore);
        typedColumns.Should().BeGreaterThan(firstImportFunction);

        var assembly = typeof(PostgresFixture).Assembly;
        foreach (var migration in migrations)
        {
            using var stream = assembly.GetManifestResourceStream(migration);
            stream.Should().NotBeNull();
            using var reader = new StreamReader(stream!);
            reader.ReadToEnd().Should().NotBeNullOrWhiteSpace();
        }
    }
}
