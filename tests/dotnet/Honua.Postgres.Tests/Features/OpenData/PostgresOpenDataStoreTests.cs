// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.OpenData.Domain;
using Honua.Postgres.Features.OpenData;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.OpenData;

[Collection("Database")]
public sealed class PostgresOpenDataStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task StoreRecords_WithSeparateStoreInstances_RoundTripsPersistedOpenDataState()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpenDataStoreTests));
        try
        {
            await EnsureTablesAsync(schema).ConfigureAwait(false);

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var writer = new PostgresOpenDataStore(provider, schema);
            var reader = new PostgresOpenDataStore(provider, schema);
            var updatedAt = DateTimeOffset.Parse("2026-05-25T10:00:00Z", CultureInfo.InvariantCulture);

            var page = new OpenDataPageRecord
            {
                ItemId = "content-1",
                Title = "Open parcels",
                Description = "Public parcel extract.",
                Publisher = new OpenDataOrganization { Name = "Honua" },
                ContactPoint = new OpenDataContact { Email = "data@example.com" },
                License = "https://creativecommons.org/licenses/by/4.0/",
                Tags = ["parcels", "open-data"],
                Distributions =
                [
                    new OpenDataDistribution
                    {
                        Title = "GeoJSON",
                        MediaType = "application/geo+json",
                        DownloadUrl = "https://example.com/parcels.geojson"
                    }
                ],
                IsPublished = true,
                UpdatedAt = updatedAt,
                UpdatedBy = "operator-1"
            };
            var publication = new OpenDataStacPublicationRecord
            {
                CollectionId = "open-parcels",
                ItemId = page.ItemId,
                Status = OpenDataStacPublicationStatus.Published,
                PublicStacCollectionUrl = "https://example.com/stac/collections/open-parcels",
                Title = "Open parcels",
                Description = "STAC projection for public parcels.",
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            };

            await writer.SetPageRecordAsync(page).ConfigureAwait(false);
            await writer.SetStacPublicationAsync(publication).ConfigureAwait(false);

            var loadedPage = await reader.GetPageRecordAsync(page.ItemId).ConfigureAwait(false);
            var listedPages = await reader.ListPageRecordsAsync().ConfigureAwait(false);
            var loadedPublication = await reader.GetStacPublicationAsync(publication.CollectionId).ConfigureAwait(false);
            var loadedByItem = await reader.GetStacPublicationByItemAsync(page.ItemId).ConfigureAwait(false);
            var listedPublications = await reader.ListStacPublicationsAsync().ConfigureAwait(false);

            loadedPage.Should().BeEquivalentTo(page);
            listedPages.Should().ContainSingle().Which.Should().BeEquivalentTo(page);
            loadedPublication.Should().BeEquivalentTo(publication);
            loadedByItem.Should().BeEquivalentTo(publication);
            listedPublications.Should().ContainSingle().Which.Should().BeEquivalentTo(publication);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema).ConfigureAwait(false);
        }
    }

    private async Task EnsureTablesAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            CREATE TABLE IF NOT EXISTS "{schema}".open_data_pages (
                item_id      TEXT        NOT NULL PRIMARY KEY,
                is_published BOOLEAN    NOT NULL DEFAULT FALSE,
                updated_at   TIMESTAMPTZ NOT NULL,
                updated_by   TEXT,
                page_json    JSONB       NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "{schema}".open_data_stac_publications (
                collection_id    TEXT        NOT NULL PRIMARY KEY,
                item_id          TEXT        NOT NULL,
                status           TEXT        NOT NULL,
                updated_at       TIMESTAMPTZ NOT NULL,
                publication_json JSONB       NOT NULL,
                CONSTRAINT open_data_stac_publications_status
                    CHECK (status IN ('Published', 'Unpublished'))
            );
            """, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken)
                    .ConfigureAwait(false);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}
