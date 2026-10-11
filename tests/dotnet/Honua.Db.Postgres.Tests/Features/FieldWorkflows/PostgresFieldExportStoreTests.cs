// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.FieldWorkflows;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FieldWorkflows;

/// <summary>
/// Drives <see cref="PostgresFieldExportStore"/> against PostGIS with a small row cap
/// so truncation is observable without inserting <see cref="FieldExportLimits.MaxRows"/> rows.
/// </summary>
[Collection("Database")]
public sealed class PostgresFieldExportStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task CreateExport_WhenMatchesExceedTheCap_KeepsNewestRowsAndMarksTruncation()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresFieldExportStoreTests));
        try
        {
            await CreateTablesAsync(schema);
            var oldest = Guid.NewGuid();
            var middle = Guid.NewGuid();
            var newest = Guid.NewGuid();
            await InsertSubmissionAsync(schema, oldest, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await InsertSubmissionAsync(schema, middle, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
            await InsertSubmissionAsync(schema, newest, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

            var store = new PostgresFieldExportStore(
                new TestConnectionProvider(fixture.DataSource, schema),
                schema,
                maxExportRows: 2);
            var package = await store.CreateExportAsync(new FieldExportRequest(), "operator");

            package.Truncated.Should().BeTrue();
            package.Items.Select(item => item.SubmissionId).Should().Equal(newest, middle);
            package.Record.RecordCount.Should().Be(2);
            using var filter = JsonDocument.Parse(package.Record.Filter);
            filter.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
            filter.RootElement.GetProperty("rowLimit").GetInt32().Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task CreateExport_WhenMatchesFitTheCap_IsNotTruncated()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresFieldExportStoreTests) + "Fit");
        try
        {
            await CreateTablesAsync(schema);
            await InsertSubmissionAsync(schema, Guid.NewGuid(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await InsertSubmissionAsync(schema, Guid.NewGuid(), new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

            var store = new PostgresFieldExportStore(
                new TestConnectionProvider(fixture.DataSource, schema),
                schema,
                maxExportRows: 2);
            var package = await store.CreateExportAsync(
                new FieldExportRequest { Format = FieldExportFormat.Csv, FormId = "form-1" },
                "operator");

            package.Truncated.Should().BeFalse();
            package.Items.Should().HaveCount(2);
            package.Record.RecordCount.Should().Be(2);
            using var filter = JsonDocument.Parse(package.Record.Filter);
            filter.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
            filter.RootElement.GetProperty("rowLimit").GetInt32().Should().Be(2);
            filter.RootElement.GetProperty("formId").GetString().Should().Be("form-1");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task CreateTablesAsync(string schema)
    {
        var quoted = SchemaSearchPath.ValidateAndQuote(schema);
        await fixture.ExecuteAsync(
            $"""
            CREATE TABLE {quoted}.form_submissions (
                submission_id UUID PRIMARY KEY,
                form_id TEXT NOT NULL,
                form_version INT NOT NULL,
                service_id TEXT NOT NULL,
                layer_id INT NOT NULL,
                target_feature_id BIGINT,
                operation TEXT NOT NULL,
                status TEXT NOT NULL,
                actor_hash TEXT NOT NULL,
                request_summary JSONB NOT NULL,
                validation_json JSONB,
                created_at TIMESTAMPTZ NOT NULL
            );
            CREATE TABLE {quoted}.form_submission_attachments (
                submission_id UUID NOT NULL,
                status TEXT NOT NULL
            );
            CREATE TABLE {quoted}.field_submission_reviews (
                submission_id UUID PRIMARY KEY,
                status TEXT NOT NULL,
                assigned_to TEXT,
                decided_by TEXT,
                decided_at TIMESTAMPTZ,
                decision_note TEXT,
                updated_at TIMESTAMPTZ NOT NULL,
                etag TEXT,
                has_conflict BOOLEAN NOT NULL
            );
            CREATE TABLE {quoted}.field_export_records (
                export_id UUID PRIMARY KEY,
                format TEXT NOT NULL,
                record_count BIGINT NOT NULL,
                filter JSONB NOT NULL,
                requested_by TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                CONSTRAINT field_export_records_valid_format CHECK (format IN ('geojson', 'csv'))
            );
            """,
            schema);
    }

    private async Task InsertSubmissionAsync(string schema, Guid submissionId, DateTimeOffset createdAt)
    {
        var quoted = SchemaSearchPath.ValidateAndQuote(schema);
        const string emptyJson = "'{}'";
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {quoted}.form_submissions (
                submission_id, form_id, form_version, service_id, layer_id,
                operation, status, actor_hash, request_summary, created_at)
            VALUES (
                @submission_id, 'form-1', 1, 'svc', 1,
                'create', 'accepted', 'actor', {emptyJson}::jsonb, @created_at)
            """,
            connection);
        command.Parameters.AddWithValue("submission_id", submissionId);
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
