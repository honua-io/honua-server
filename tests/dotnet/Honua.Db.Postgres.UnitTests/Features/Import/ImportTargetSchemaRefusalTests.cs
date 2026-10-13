// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// SEC-23: Postgres import writers accept only the configured operational schemas as a target, and
/// refuse anything else before they read the source or open a connection.
/// </summary>
public sealed class ImportTargetSchemaRefusalTests
{
    private const string UnreachableConnectionString = "Host=unreachable.invalid;Database=none;Username=none;Password=none";

    private static readonly PostgresSchemaConfiguration SchemaConfiguration = new(
        "catalog_meta",
        PostgresSchemaConfiguration.DefaultDataSchema,
        [PostgresSchemaConfiguration.DefaultDataSchema, "public", "ops", "catalog_meta"]);

    [UnitTheory]
    [InlineData("staging")]
    [InlineData("honua")]
    [InlineData("catalog_meta")]
    [InlineData("pg_catalog")]
    [InlineData("information_schema")]
    public void ResolveImportTargetSchema_SchemaOutsideOperationalList_Throws(string schema)
    {
        // catalog_meta is listed as operational but is the metadata schema, so it stays refused.
        var act = () => SchemaConfiguration.ResolveImportTargetSchema(schema);

        act.Should().Throw<ArgumentException>().WithMessage(ImportTargetSchemaPolicy.NotOperationalSchemaMessage + "*");
    }

    [UnitTheory]
    [InlineData(null, "honua_data")]
    [InlineData("  ", "honua_data")]
    [InlineData("public", "public")]
    [InlineData(" ops ", "ops")]
    [InlineData("OPS", "ops")]
    public void ResolveImportTargetSchema_OperationalOrOmittedSchema_Resolves(string? requested, string expected)
    {
        SchemaConfiguration.ResolveImportTargetSchema(requested).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("honua")]
    [InlineData("staging")]
    public async Task FileImport_TargetSchemaOutsideOperationalList_RefusedBeforeConnecting(string targetSchema)
    {
        // ThrowingConnectionProvider fails any database access with NotSupportedException, so an
        // ArgumentException proves the refusal happened first.
        var service = new StreamingFileImportService(
            new ThrowingConnectionProvider(),
            new NoopCrsDetectionService(),
            new TestFileFormatDetectionService(),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance,
            schemaConfiguration: SchemaConfiguration);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"type":"FeatureCollection","features":[]}"""));

        var act = () => service.ImportFileAsync(
            new ImportRequest
            {
                FileStream = source,
                FileName = "points.geojson",
                TableName = "points",
                TargetSchema = targetSchema,
                OverwriteExisting = true
            },
            progress: null);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(ImportTargetSchemaPolicy.NotOperationalSchemaMessage + "*");
    }

    [UnitTheory]
    [InlineData("honua", "honua_data")]
    [InlineData("honua_data", "catalog_meta")]
    [InlineData("pg_catalog", "honua_data")]
    public async Task MigrationFeatureCopy_ReservedSourceOrTargetSchema_RefusedBeforeConnecting(
        string sourceSchema,
        string targetSchema)
    {
        var writer = new PostgresMigrationCatalogWriter(
            NullLogger<PostgresMigrationCatalogWriter>.Instance,
            SchemaConfiguration);

        var act = () => writer.CopyFeatureDataAsync(
            UnreachableConnectionString,
            new MigrationFeatureCopyRequest
            {
                SourceSchema = sourceSchema,
                SourceTable = "roads",
                TargetSchema = targetSchema,
                TargetTable = "roads"
            });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(ImportTargetSchemaPolicy.ReservedSchemaMessage + "*");
    }

    [UnitTheory]
    [InlineData("catalog_meta")]
    [InlineData("staging")]
    public async Task OgcApiFeaturesSink_TargetSchemaOutsideOperationalList_RefusedBeforeConnecting(string targetSchema)
    {
        await using var dataSource = NpgsqlDataSource.Create(UnreachableConnectionString);
        var sink = new PostgresOgcApiFeaturesCollectionSink(
            dataSource,
            NullLogger<PostgresOgcApiFeaturesCollectionSink>.Instance,
            SchemaConfiguration);

        var act = () => sink.EnsureTargetAsync(
            new OgcApiFeaturesSinkTarget { Schema = targetSchema, Table = "roads", CollectionId = "roads" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(ImportTargetSchemaPolicy.NotOperationalSchemaMessage + "*");
    }
}
