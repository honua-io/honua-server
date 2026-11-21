// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Each case uses a pool of its own, so the schema it returns is the one it gets back, and drops
// whatever that pool still holds when it ends.
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class ControlPlaneSchemaPoolTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task ReusedSchema_HoldsTheFreshlyMigratedRowsAndSequencePositions()
    {
        var pool = new ControlPlaneSchemaPool();
        try
        {
            var schema = await ReturnChangedRowsAsync(pool);

            await using var reused = await ControlPlaneStateHarness.CreateAsync(database, nameof(ControlPlaneSchemaPoolTests), pool);

            reused.Schema.Should().Be(schema, "the pool hands the returned schema to the next case");
            (await ControlPlaneSchemaCatalog.DescribeContentAsync(database.DataSource, reused.Schema))
                .Should().Be(pool.FreshContent(database));
            (await reused.ScalarAsync<long>($"SELECT generation FROM {reused.Table("configuration_generation")}"))
                .Should().Be(0, "the migration's generation row is back at its first value");
        }
        finally
        {
            await pool.DropIdleAsync(database);
        }
    }

    [IntegrationTest]
    public async Task SchemaWithAChangedCatalog_IsDroppedInsteadOfReused()
    {
        var pool = new ControlPlaneSchemaPool();
        try
        {
            string schema;
            await using (var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(ControlPlaneSchemaPoolTests), pool))
            {
                schema = harness.Schema;
                await harness.ExecuteAsync($"CREATE TABLE \"{schema}\".case_added (id integer)");
            }

            (await ControlPlaneSchemaCatalog.ExistsAsync(database.DataSource, schema)).Should().BeFalse();
        }
        finally
        {
            await pool.DropIdleAsync(database);
        }
    }

    [IntegrationTest]
    public async Task HarnessDisposedTwice_IsHandedToOneLaterCaseOnly()
    {
        var pool = new ControlPlaneSchemaPool();
        try
        {
            var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(ControlPlaneSchemaPoolTests), pool);
            await harness.DisposeAsync();
            await harness.DisposeAsync();

            await using var first = await ControlPlaneStateHarness.CreateAsync(database, nameof(ControlPlaneSchemaPoolTests), pool);
            await using var second = await ControlPlaneStateHarness.CreateAsync(database, nameof(ControlPlaneSchemaPoolTests), pool);

            first.Schema.Should().NotBe(second.Schema);
        }
        finally
        {
            await pool.DropIdleAsync(database);
        }
    }

    // Writes a configuration document (which advances the generation row and the revision identity),
    // returns the schema to the pool and reports its name.
    private async Task<string> ReturnChangedRowsAsync(ControlPlaneSchemaPool pool)
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(ControlPlaneSchemaPoolTests), pool);
        var fresh = pool.FreshContent(database);
        (await ControlPlaneSchemaCatalog.DescribeContentAsync(database.DataSource, harness.Schema)).Should().Be(fresh);

        await harness.ExecuteAsync(
            $"INSERT INTO {harness.Table("configuration_documents")} (kind, key, payload) VALUES ('pool-check', 'one', '\\x00'::bytea)");

        (await ControlPlaneSchemaCatalog.DescribeContentAsync(database.DataSource, harness.Schema)).Should().NotBe(fresh);
        return harness.Schema;
    }
}
