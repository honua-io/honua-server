// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class StreamingFileImportStagingTableTests
{
    [IntegrationTheory]
    [InlineData("success")]
    [InlineData("receipt-failure")]
    public async Task ImportFileAsync_CreateOnly_PreservesImportedStagingNamedSibling(string outcome)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_sibling");
        try
        {
            await EnsureImportFunctionsAsync();
            await SeedOverwriteGuardTargetAsync(schema);
            await MarkImporterOwnedAsync(schema, OverwriteGuardPhysicalTable);
            var identity = await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable);
            var before = await ReadOverwriteGuardRowsAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var rename = new NpgsqlCommand(
                $"ALTER TABLE \"{schema}\".\"{OverwriteGuardPhysicalTable}\" RENAME TO \"{OverwriteGuardPhysicalTable}__staging\"", connection);
            await rename.ExecuteNonQueryAsync();

            var result = await ImportCreateOnlyAsync(schema, commitParticipant: outcome == "receipt-failure"
                ? (_, _) => throw new IOException("receipt persistence failed") : null);

            result.Success.Should().Be(outcome == "success");
            (await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable + "__staging")).Should().Be(identity);
            rename.CommandText = $"ALTER TABLE \"{schema}\".\"{OverwriteGuardPhysicalTable}__staging\" RENAME TO \"preserved_sibling\"";
            await rename.ExecuteNonQueryAsync();
            (await ReadOverwriteGuardRowsAsync(schema, "preserved_sibling")).Should().Equal(before);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyFeatureLimit_DiscardsDatasetWithoutReceipt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_limit");
        try
        {
            await EnsureImportFunctionsAsync();
            var receiptWritten = false;
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
            var result = await CreateOnlyService(schema, ImportLimits.Default with
            {
                MaxFeaturesPerFile = 1,
                BatchSize = 1,
            }).ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "points.geojson",
                TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.CreateOnly,
                CommitParticipant = (_, _) => { receiptWritten = true; return ValueTask.CompletedTask; },
            });

            result.Success.Should().BeFalse();
            receiptWritten.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyCompletionCallbackFails_PreservesCommittedSuccess()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_completion");
        try
        {
            await EnsureImportFunctionsAsync();
            var receiptWritten = false;
            var progress = new InlineImportProgress(value =>
            {
                if (value.Status == ImportStatus.Completed)
                {
                    throw new IOException("notification unavailable");
                }
            });

            var result = await ImportCreateOnlyAsync(schema, progress,
                (_, _) => { receiptWritten = true; return ValueTask.CompletedTask; });

            result.Success.Should().BeTrue();
            receiptWritten.Should().BeTrue();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeTrue();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
