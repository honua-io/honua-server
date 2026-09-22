// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Issue #4600, acceptance criterion 5, after #4742: staging cleanup, explicit partial-state and
/// restart semantics, one writer per target, and source changes during the transfer. These run
/// against real PostGIS. The oracles are the seeded prior rows, the PostgreSQL catalog read from a
/// separate session, and the record populations the mock source is told to serve — never the
/// importer's own output.
/// </summary>
[Collection("Database")]
public sealed class GeoservicesImportTransferSemanticsTests(PostgresFixture fixture)
{
    private const string TableName = "geoservices_transfer_target";

    /// <summary>
    /// Before #4600 a replacement dropped the prior target at the start of its transaction, holding an
    /// exclusive lock on the live table for the whole transfer: every reader blocked until the import
    /// finished. A second session now reads the prior rows mid-transfer under a short lock timeout.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_ReplacingExistingTarget_KeepsPriorTargetReadableWhileTransferring()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferReadable");
        try
        {
            await SeedPriorTargetAsync(schemaName);
            string[]? codesReadMidTransfer = null;
            string? midTransferReadError = null;
            string? midTransferWriteOutcome = null;
            var handler = new TransferFeatureServerHandler(["CCC", "DDD"])
            {
                SupportsPagination = false,
                OnObjectIdWindow = objectId =>
                {
                    if (objectId != 2)
                    {
                        return;
                    }

                    try
                    {
                        codesReadMidTransfer = ReadCodesWithLockTimeout(schemaName);
                    }
                    catch (PostgresException ex)
                    {
                        midTransferReadError = $"{ex.SqlState}: {ex.MessageText}";
                        return;
                    }

                    // An edit committed now would be acknowledged and then discarded by the swap, so it
                    // must wait for the replacement instead (here: fail its short lock timeout).
                    using var writer = fixture.DataSource.OpenConnection();
                    using (var setTimeout = new NpgsqlCommand("SET lock_timeout = '2s'", writer))
                    {
                        setTimeout.ExecuteNonQuery();
                    }

                    using var update = new NpgsqlCommand(
                        $"""UPDATE "{schemaName}"."{TableName}" SET code = 'ZZZ' WHERE code = 'AAA'""",
                        writer);
                    try
                    {
                        update.ExecuteNonQuery();
                        midTransferWriteOutcome = "committed against the prior target";
                    }
                    catch (PostgresException ex)
                    {
                        midTransferWriteOutcome = ex.SqlState;
                    }
                }
            };

            var result = await CreateService(handler).ImportLayerAsync(BuildRequest(schemaName) with { BatchSize = 1 });

            midTransferReadError.Should().BeNull("readers of the live target must not block while the replacement transfers");
            codesReadMidTransfer.Should().Equal(["AAA", "BBB"], "readers see the complete prior target until the swap");
            midTransferWriteOutcome.Should().Be(
                PostgresErrorCodes.LockNotAvailable,
                "writes to the prior target are fenced until the swap so no acknowledged edit is discarded");
            result.Success.Should().BeTrue(result.ErrorMessage);
            (await ReadCodesAsync(schemaName)).Should().Equal("CCC", "DDD");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// A committed replacement leaves exactly the relations a first import creates: no staging table,
    /// primary key and sequence carrying the target's names, and a sequence that still feeds the key.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_ReplacingExistingTarget_LeavesOnlyTheTargetsOwnRelations()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferRelations");
        try
        {
            await SeedPriorTargetAsync(schemaName);

            var result = await CreateService(new TransferFeatureServerHandler(["CCC", "DDD"]))
                .ImportLayerAsync(BuildRequest(schemaName));

            result.Success.Should().BeTrue(result.ErrorMessage);
            (await ReadRelationsAsync(schemaName)).Should().Equal(
                ($"{TableName}", "r"),
                ($"{TableName}_geom_idx", "i"),
                ($"{TableName}_objectid_seq", "S"),
                ($"{TableName}_pkey", "i"));

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var insert = new NpgsqlCommand(
                $"""INSERT INTO "{schemaName}"."{TableName}" (code) VALUES ('EEE') RETURNING objectid""",
                connection);
            (await insert.ExecuteScalarAsync()).Should().Be(3L, "the renamed sequence still owns the key after two imported rows");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// Staging cleanup on both abort paths: a refused replacement (a record failed to load) and a
    /// cancellation mid-transfer leave the prior target's relations exactly as seeded.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_ReplacementRefusedOrCancelled_LeavesNoStagingRelations()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferCleanup");
        try
        {
            await SeedPriorTargetAsync(schemaName);
            var seededRelations = await ReadRelationsAsync(schemaName);

            var refused = await CreateService(new TransferFeatureServerHandler(["CCC", "TOOLONG"]))
                .ImportLayerAsync(BuildRequest(schemaName));

            refused.Success.Should().BeFalse();
            refused.ErrorMessage.Should().Contain("Replacement refused");
            (await ReadRelationsAsync(schemaName)).Should().Equal(seededRelations);

            using var cancellation = new CancellationTokenSource();
            var cancelling = new TransferFeatureServerHandler(["EEE", "FFF"])
            {
                SupportsPagination = false,
                OnObjectIdWindow = objectId =>
                {
                    if (objectId == 2)
                    {
                        cancellation.Cancel();
                    }
                }
            };

            var act = () => CreateService(cancelling).ImportLayerAsync(
                BuildRequest(schemaName) with { BatchSize = 1 },
                progress: null,
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            (await ReadRelationsAsync(schemaName)).Should().Equal(seededRelations);
            (await ReadCodesAsync(schemaName)).Should().Equal("AAA", "BBB");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// One writer per target. A second job for the same table, started while the first is mid-transfer
    /// (a retry submitted too early, or two operators), fails at once with an explicit message instead
    /// of blocking on the first job's locks or replacing what it commits.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenAnotherImportIsWritingTheSameTarget_FailsWithoutTouchingIt()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferSingleWriter");
        try
        {
            await SeedPriorTargetAsync(schemaName);
            var competingService = CreateService(new TransferFeatureServerHandler(["XXX"]));
            GeoservicesImportResult? competing = null;
            string? competingError = null;
            var handler = new TransferFeatureServerHandler(["CCC", "DDD"])
            {
                SupportsPagination = false,
                OnObjectIdWindow = objectId =>
                {
                    if (objectId != 2)
                    {
                        return;
                    }

                    // Bounded: before #4600 the competing job blocked on the first job's locks while the
                    // first job waited here for it, so the wait is cut off rather than hanging the test.
                    using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try
                    {
                        competing = competingService
                            .ImportLayerAsync(BuildRequest(schemaName) with { JobId = "competing" }, progress: null, bounded.Token)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        competingError = "the competing import blocked until it was cancelled";
                    }
                }
            };

            var result = await CreateService(handler).ImportLayerAsync(
                BuildRequest(schemaName) with { BatchSize = 1, JobId = "original" });

            competingError.Should().BeNull();
            competing.Should().NotBeNull();
            competing!.Success.Should().BeFalse();
            competing.NeedsReview.Should().BeFalse();
            competing.ErrorMessage.Should().Be(
                $"Another import is already writing target table '{TableName}'; this job did not modify it. "
                + "Wait for that import to finish, then retry.");

            result.Success.Should().BeTrue(result.ErrorMessage);
            (await ReadCodesAsync(schemaName)).Should().Equal("CCC", "DDD");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// Restart semantics: an import that is not a replacement, pointed at a table that already exists
    /// (including a job restarted after its data committed), fails explicitly and leaves the table as
    /// it was. Before #4600 it failed inside CREATE TABLE with only a generic message.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WithoutOverwriteIntoAnExistingTable_FailsExplicitlyAndLeavesItUnchanged()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferNoOverwrite");
        try
        {
            await SeedPriorTargetAsync(schemaName);

            var result = await CreateService(new TransferFeatureServerHandler(["CCC"]))
                .ImportLayerAsync(BuildRequest(schemaName) with { OverwriteExisting = false });

            result.Success.Should().BeFalse();
            result.NeedsReview.Should().BeFalse();
            result.ErrorMessage.Should().StartWith(
                $"Target table '{TableName}' already exists and overwriteExisting is false, so it was not modified.");
            (await ReadCodesAsync(schemaName)).Should().Equal("AAA", "BBB");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// Offset-paged transfer: the source advertises 2 records at discovery and 3 once the last page has
    /// been read. The run commits what it read but must not claim a faithful snapshot.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenSourceCountChangesDuringPagedTransfer_RoutesToReviewWithBothCounts()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferSourceGrew");
        try
        {
            var handler = new TransferFeatureServerHandler(["CCC", "DDD"]) { CountAfterDiscovery = 3 };

            var result = await CreateService(handler).ImportLayerAsync(BuildRequest(schemaName));

            result.Success.Should().BeFalse();
            result.NeedsReview.Should().BeTrue();
            result.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Incomplete);
            var difference = result.FidelityDifferences.Should().ContainSingle().Subject;
            difference.Code.Should().Be(MigrationFidelityDifferenceCodes.SourceChangedDuringTransfer);
            difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
            difference.Expected.Should().Be("2 source records when the transfer started");
            difference.Actual.Should().Be("3 source records when the transfer finished");
            (await ReadCodesAsync(schemaName)).Should().Equal("CCC", "DDD");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// Object-id window transfer: record 2 is deleted and record 3 added mid-transfer. The count is
    /// unchanged, so only the object-ID set comparison can see it.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenSourceObjectIdsChangeDuringWindowedTransfer_RoutesToReview()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferSourceSwapped");
        try
        {
            var handler = new TransferFeatureServerHandler(["CCC", "DDD"])
            {
                SupportsPagination = false,
                ObjectIdsAfterFirstEnumeration = [1, 3]
            };

            var result = await CreateService(handler).ImportLayerAsync(BuildRequest(schemaName) with { BatchSize = 1 });

            result.NeedsReview.Should().BeTrue();
            var difference = result.FidelityDifferences.Should().ContainSingle().Subject;
            difference.Code.Should().Be(MigrationFidelityDifferenceCodes.SourceChangedDuringTransfer);
            difference.Expected.Should().Be("2 source records when the transfer started");
            difference.Actual.Should().Be("2 source records when the transfer finished (different object IDs)");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// When the source cannot be re-counted after the transfer, a change cannot be ruled out: the run
    /// completes, but as unverified rather than full fidelity.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenSourceCannotBeRecountedAfterTransfer_CompletesUnverified()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferRecountFailed");
        try
        {
            var handler = new TransferFeatureServerHandler(["CCC", "DDD"]) { FailCountAfterDiscovery = true };

            var result = await CreateService(handler).ImportLayerAsync(BuildRequest(schemaName));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Unverified);
            var difference = result.FidelityDifferences.Should().ContainSingle().Subject;
            difference.Code.Should().Be(MigrationFidelityDifferenceCodes.SourceSnapshotUnverified);
            difference.Actual.Should().Be("source record count unavailable after the transfer");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// A target name at PostgreSQL's 63-byte limit, where the derived primary-key and sequence names
    /// are shortened. A replacement must leave exactly the relation names a first import of that
    /// table creates, and the same job replacing it again must still succeed. The oracle is the first
    /// import's own catalog entries.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_ReplacingALongNamedTargetRepeatedly_KeepsFirstImportRelationNames()
    {
        const string prefix = "geoservices_transfer_target_";
        var longTable = prefix + new string('x', 63 - prefix.Length);
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferLongName");
        try
        {
            var request = BuildRequest(schemaName) with { TableName = longTable, JobId = "long-name-job" };
            var first = await CreateService(new TransferFeatureServerHandler(["AAA", "BBB"]))
                .ImportLayerAsync(request with { JobId = "first-import" });
            first.Success.Should().BeTrue(first.ErrorMessage);
            var firstImportRelations = await ReadRelationsAsync(schemaName);
            firstImportRelations.Should().HaveCount(4, "a first import creates the table, its key, its sequence and its spatial index");

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var replacement = await CreateService(new TransferFeatureServerHandler(["CCC", "DDD"]))
                    .ImportLayerAsync(request);

                replacement.Success.Should().BeTrue($"replacement {attempt} by the same job must succeed: {replacement.ErrorMessage}");
                (await ReadRelationsAsync(schemaName)).Should().Equal(
                    firstImportRelations,
                    $"replacement {attempt} must leave the relation names a first import creates");
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// The lease covers the whole job, not only the data transaction. A competing import started after
    /// the first job committed but before it finished must still be refused: publishing, attachment
    /// copy and reconciliation run in that window. The final progress report follows the commit.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenAnotherImportStartsAfterCommitBeforeTheJobFinishes_RefusesIt()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferPostCommitLease");
        try
        {
            await SeedPriorTargetAsync(schemaName);
            var competingService = CreateService(new TransferFeatureServerHandler(["XXX"]));
            GeoservicesImportResult? competing = null;
            var progress = new SynchronousProgress(update =>
            {
                if (update.Status != Honua.Core.Features.Migration.Abstractions.GeoservicesImportStatus.Completed
                    || competing is not null)
                {
                    return;
                }

                using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                competing = competingService
                    .ImportLayerAsync(BuildRequest(schemaName) with { JobId = "competing" }, progress: null, bounded.Token)
                    .GetAwaiter()
                    .GetResult();
            });

            var result = await CreateService(new TransferFeatureServerHandler(["CCC", "DDD"]))
                .ImportLayerAsync(BuildRequest(schemaName) with { JobId = "original" }, progress);

            result.Success.Should().BeTrue(result.ErrorMessage);
            competing.Should().NotBeNull("the completed progress report is emitted after the commit");
            competing!.Success.Should().BeFalse();
            competing.ErrorMessage.Should().StartWith($"Another import is already writing target table '{TableName}'");
            (await ReadCodesAsync(schemaName)).Should().Equal(["CCC", "DDD"], "the competing import must not replace the committed table");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// A post-transfer re-count that exceeds the per-request timeout is an unreadable snapshot, not a
    /// cancellation of the import, so the run completes as unverified.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenSourceRecountTimesOut_CompletesUnverifiedInsteadOfFailing()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferRecountTimeout");
        try
        {
            var handler = new TransferFeatureServerHandler(["CCC", "DDD"]) { DelayCountAfterDiscovery = TimeSpan.FromSeconds(5) };

            var result = await CreateService(handler).ImportLayerAsync(BuildRequest(schemaName) with { RequestTimeoutSeconds = 1 });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Unverified);
            result.FidelityDifferences.Should().ContainSingle()
                .Which.Code.Should().Be(MigrationFidelityDifferenceCodes.SourceSnapshotUnverified);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// #4827 REQ-003: when the import's own database session is lost mid-transfer, the failure-path
    /// rollback cannot run on that session. The job must still report the originating database
    /// failure as a specific, redacted reason instead of letting the rollback error replace it, and
    /// the prior target must be untouched.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenImportSessionIsLostMidTransfer_ReportsDatabaseFailureInsteadOfRollbackError()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportTransferSessionLost");
        var applicationName = $"import-session-lost-{Guid.NewGuid():N}"[..40];
        try
        {
            await SeedPriorTargetAsync(schemaName);
            var terminated = 0;
            var handler = new TransferFeatureServerHandler(["CCC", "DDD"])
            {
                SupportsPagination = false,
                OnObjectIdWindow = objectId =>
                {
                    if (objectId == 2)
                    {
                        terminated = TerminateSessions(applicationName);
                    }
                }
            };

            var service = CreateService(handler, applicationName);
            var result = await service.ImportLayerAsync(BuildRequest(schemaName) with { BatchSize = 1 });

            terminated.Should().Be(1, "the test ends exactly the import's own session");
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().StartWith(
                "ARCGIS_IMPORT_DATABASE_UNAVAILABLE:",
                "the lost session is the originating failure, not the rollback that could not run on it");
            result.ErrorMessage.Should().Contain("No imported data was committed");
            result.ErrorMessage.Should().NotContainAny(
                "terminating connection",
                "NpgsqlTransaction",
                "no longer usable",
                new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Host!);
            (await ReadCodesAsync(schemaName)).Should().Equal(["AAA", "BBB"], "the prior target is never touched by a failed replacement");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    // Synchronous on purpose: it runs inside the mock source's request handler, mid-transfer.
    private int TerminateSessions(string applicationName)
    {
        using var connection = fixture.DataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = @name",
            connection);
        command.Parameters.AddWithValue("name", applicationName);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class SynchronousProgress(Action<Honua.Core.Features.Migration.Abstractions.GeoservicesImportProgress> onReport)
        : IProgress<Honua.Core.Features.Migration.Abstractions.GeoservicesImportProgress>
    {
        public void Report(Honua.Core.Features.Migration.Abstractions.GeoservicesImportProgress value) => onReport(value);
    }

    private static GeoservicesImportRequest BuildRequest(string schemaName) => new()
    {
        ServiceUrl = "https://example.com/arcgis/rest/services/Transfer/FeatureServer",
        LayerId = 0,
        TableName = TableName,
        TargetSchema = schemaName,
        TargetSrid = 4326,
        BatchSize = 10,
        RequestTimeoutSeconds = 5,
        MaxRetries = 0,
        OverwriteExisting = true,
        AutoPublish = false,
        ImportAttachments = false
    };

    private GeoservicesImportService CreateService(HttpMessageHandler handler, string? applicationName = null)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(handler),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        return new GeoservicesImportService(
            restClient,
            new FixtureConnectionProvider(fixture, applicationName),
            new Mock<ICrsRegistry>(MockBehavior.Loose).Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            new GeoservicesLayerPublicationService(NullLogger<GeoservicesLayerPublicationService>.Instance));
    }

    private async Task SeedPriorTargetAsync(string schemaName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            CREATE TABLE "{schemaName}"."{TableName}" (
                objectid BIGSERIAL PRIMARY KEY,
                code VARCHAR(3),
                geom geometry(Point, 4326));
            INSERT INTO "{schemaName}"."{TableName}" (code, geom) VALUES
                ('AAA', ST_SetSRID(ST_MakePoint(-157.0, 21.0), 4326)),
                ('BBB', ST_SetSRID(ST_MakePoint(-157.5, 21.5), 4326));
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> ReadCodesAsync(string schemaName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT code FROM "{schemaName}"."{TableName}" ORDER BY code""",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var codes = new List<string>();
        while (await reader.ReadAsync())
        {
            codes.Add(reader.GetString(0));
        }

        return codes.ToArray();
    }

    // Synchronous on purpose: it runs inside the mock source's request handler, mid-transfer.
    private string[] ReadCodesWithLockTimeout(string schemaName)
    {
        using var connection = fixture.DataSource.OpenConnection();
        using (var setTimeout = new NpgsqlCommand("SET lock_timeout = '2s'", connection))
        {
            setTimeout.ExecuteNonQuery();
        }

        using var command = new NpgsqlCommand(
            $"""SELECT code FROM "{schemaName}"."{TableName}" ORDER BY code""",
            connection);
        using var reader = command.ExecuteReader();
        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes.ToArray();
    }

    private async Task<(string Name, string Kind)[]> ReadRelationsAsync(string schemaName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, c.relkind::text
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
            ORDER BY c.relname
            """,
            connection);
        command.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await command.ExecuteReaderAsync();
        var relations = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            relations.Add((reader.GetString(0), reader.GetString(1)));
        }

        return relations.ToArray();
    }

    /// <summary>
    /// ArcGIS FeatureServer mock serving one point layer whose records carry the supplied <c>code</c>
    /// values, with OBJECTIDs 1..n. The first count and the first object-ID enumeration describe that
    /// population; later ones can be overridden to model a source edited mid-transfer.
    /// </summary>
    private sealed class TransferFeatureServerHandler(string[] codes) : HttpMessageHandler
    {
        private int _countRequests;
        private int _objectIdEnumerations;

        public bool SupportsPagination { get; init; } = true;

        public Action<long>? OnObjectIdWindow { get; init; }

        public int? CountAfterDiscovery { get; init; }

        public bool FailCountAfterDiscovery { get; init; }

        public long[]? ObjectIdsAfterFirstEnumeration { get; init; }

        public TimeSpan? DelayCountAfterDiscovery { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (DelayCountAfterDiscovery is { } delay
                && pathAndQuery.Contains("returnCountOnly=true", StringComparison.Ordinal)
                && Volatile.Read(ref _countRequests) >= 1)
            {
                // Longer than the request timeout: the client's own timeout cancels this wait.
                await Task.Delay(delay, cancellationToken);
            }

            string payload;
            if (pathAndQuery.EndsWith("/FeatureServer/0?f=json", StringComparison.Ordinal))
            {
                payload = $$"""
                    {
                      "id": 0,
                      "name": "Transfer Layer",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "hasAttachments": false,
                      "advancedQueryCapabilities": { "supportsPagination": {{(SupportsPagination ? "true" : "false")}} },
                      "extent": { "xmin": -158, "ymin": 21, "xmax": -157, "ymax": 22, "spatialReference": { "wkid": 4326 } },
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "code", "type": "esriFieldTypeString", "length": 3, "nullable": true }
                      ]
                    }
                    """;
            }
            else if (pathAndQuery.Contains("returnCountOnly=true", StringComparison.Ordinal))
            {
                var later = Interlocked.Increment(ref _countRequests) > 1;
                if (later && FailCountAfterDiscovery)
                {
                    throw new HttpRequestException("Source count unavailable.");
                }

                payload = $$"""{ "count": {{(later && CountAfterDiscovery is { } count ? count : codes.Length)}} }""";
            }
            else if (pathAndQuery.Contains("returnIdsOnly=true", StringComparison.Ordinal))
            {
                var later = Interlocked.Increment(ref _objectIdEnumerations) > 1;
                var ids = later && ObjectIdsAfterFirstEnumeration is { } changed
                    ? changed
                    : Enumerable.Range(1, codes.Length).Select(static id => (long)id).ToArray();
                payload = $$"""{ "objectIdFieldName": "OBJECTID", "objectIds": [{{string.Join(",", ids)}}] }""";
            }
            else if (TryReadObjectIdWindow(pathAndQuery, out var objectId))
            {
                OnObjectIdWindow?.Invoke(objectId);
                payload = FeaturePage([objectId]);
            }
            else if (pathAndQuery.Contains("resultOffset=0", StringComparison.Ordinal))
            {
                payload = FeaturePage(Enumerable.Range(1, codes.Length).Select(static id => (long)id).ToArray());
            }
            else if (pathAndQuery.Contains("resultOffset=", StringComparison.Ordinal))
            {
                payload = FeaturePage([]);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected ArcGIS request path: {pathAndQuery}");
            }

            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that invokes
            // this handler; it is disposed by the caller, not here (cs/local-not-disposed false positive).
            return new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }

        private string FeaturePage(long[] objectIds)
        {
            var features = objectIds.Select(id => $$"""
                {
                  "attributes": { "OBJECTID": {{id}}, "code": "{{codes[id - 1]}}" },
                  "geometry": { "x": -157.{{id}}, "y": 21.{{id}} }
                }
                """);
            return $$"""
                {
                  "features": [{{string.Join(",", features)}}],
                  "exceededTransferLimit": false,
                  "spatialReference": { "wkid": 4326 }
                }
                """;
        }

        private static bool TryReadObjectIdWindow(string pathAndQuery, out long objectId)
        {
            objectId = 0;
            const string marker = "objectIds=";
            var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            var end = pathAndQuery.IndexOf('&', start);
            var value = Uri.UnescapeDataString(end < 0 ? pathAndQuery[start..] : pathAndQuery[start..end]);
            return long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out objectId);
        }
    }

    /// <param name="postgresFixture">The shared PostGIS fixture.</param>
    /// <param name="applicationName">
    /// When set, connections are opened outside the fixture's pool under this application name, so a
    /// test can find (and end) exactly the importer's own session.
    /// </param>
    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture, string? applicationName = null)
        : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString()
            => new NpgsqlConnectionStringBuilder(postgresFixture.ConnectionString)
            {
                SearchPath = "honua,public"
            }.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (applicationName is null)
            {
                return await postgresFixture.DataSource.OpenConnectionAsync(cancellationToken);
            }

            var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(postgresFixture.ConnectionString)
            {
                ApplicationName = applicationName,
                Pooling = false
            }.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
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
