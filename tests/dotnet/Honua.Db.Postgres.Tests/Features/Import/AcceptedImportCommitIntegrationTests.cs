// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Actual streaming importer, migrations and PostgreSQL transactions. The commit authority
/// is a controlled Core port; these tests do not claim Host credential/lease verification.
/// </summary>
[Collection("Database")]
public sealed class AcceptedImportCommitIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string OperationId = "0123456789abcdef0123456789abcdef";
    private const string GeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"code":"a"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"code":"b"}}
        ]}
        """;
    private NpgsqlDataSource _dataSource = null!;
    private string _database = null!;
    private IAdoNetDatabaseConnectionProvider _connections = null!;
    private readonly PostgresSchemaConfiguration _schemas = new("honua", "honua_data", ["honua_data"]);

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(nameof(AcceptedImportCommitIntegrationTests));
        _database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        _dataSource = NpgsqlDataSource.Create(connectionString);
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !Directory.Exists(Path.Join(root.FullName, "src", "Honua.Server", "Migrations")))
            root = root.Parent;
        Assert.NotNull(root);
        foreach (var migration in new[] { "004_CreateImportFunctions.sql", "070_AddImportLoadModes.sql",
            "071_FixImportStagingTableNameLength.sql", "108_FixLongImportIndexNames.sql",
            "111_PreserveImportOrdinateDimensions.sql", "123_ImportDatumTransformPipeline.sql",
            "129_ProtectImportTableOwnership.sql", "136_CreateImportCommitReceipts.sql",
            "139_FixImportCommitReceiptFingerprintConstraint.sql" })
        {
            var sql = await File.ReadAllTextAsync(Path.Join(root.FullName, "src", "Honua.Server", "Migrations", migration));
            await ExecuteAsync(sql.Replace("$HonuaSchema$", "honua", StringComparison.Ordinal));
        }
        _connections = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        _connections.GetConnectionString().Returns(connectionString);
        _connections.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await _dataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await fixture.DropDatabaseAsync(_database);
    }

    [IntegrationTheory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Replace)]
    [InlineData(ImportLoadMode.Upsert)]
    public async Task NewDestination_CommitsRowsAndExactOriginalResultTogether(ImportLoadMode mode)
    {
        var job = CreateJob();
        var authority = new CommitAuthority(job);
        using var scope = JobSecurityScope.BeginExecution(job, job.Audit.SubmitterSecurityContext!);
        var result = await ImportAsync(authority, mode);
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FeatureCount.Should().Be(2);
        result.PhysicalTableName.Should().Be("imported_roads");
        authority.Checks.Should().Be(3);
        (await ScalarAsync("SELECT count(*) FROM honua_data.imported_roads")).Should().Be(2L);
        (await ScalarAsync("SELECT count(*) FROM honua_data.imported_roads WHERE ST_SRID(geometry) = 4326")).Should().Be(2L);
        var recovered = await ReadAsync(job);
        recovered.Should().BeEquivalentTo(result);
        // Recovery uses a fresh connection and no source stream, importer or second mutation.
        (await ReadAsync(job)).Should().BeEquivalentTo(result);
        (await ScalarAsync("SELECT count(*) FROM honua_data.imported_roads")).Should().Be(2L);
    }

    [IntegrationTest]
    public async Task ReplacedAttempt_AfterAuthoritativeAbsenceCannotCommitWhenDecisionLockBecomesAvailable()
    {
        // The claim authority is controlled; both the original importer and the
        // absence reader use the actual PostgreSQL decision fence. Host tests
        // separately verify that a protected principal cannot forge this claim.
        var job = CreateJob() with { ClaimedBy = "original", AttemptCount = 1 };
        var currentAttempt = 1;
        var firstCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeOriginal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var authority = new CommitAuthority(job)
        {
            BeforeCheck = async check =>
            {
                if (Volatile.Read(ref currentAttempt) != job.AttemptCount)
                    throw new UnauthorizedAccessException("The original worker claim was replaced.");
                if (check == 1)
                {
                    firstCheck.SetResult();
                    await resumeOriginal.Task.WaitAsync(deadline.Token);
                }
            }
        };
        using var scope = JobSecurityScope.BeginExecution(job, job.Audit.SubmitterSecurityContext!);
        var importing = ImportAsync(authority, cancellationToken: deadline.Token);
        await using var fence = await _dataSource.OpenConnectionAsync(deadline.Token);
        await using var transaction = await fence.BeginTransactionAsync(deadline.Token);
        try
        {
            await firstCheck.Task.WaitAsync(deadline.Token);
            Interlocked.Exchange(ref currentAttempt, 2);
            // Replacement claim precedes the real read: no original transaction
            // has committed, and the fresh reader establishes authoritative absence.
            Assert.Null(await ReadAsync(job));
            await PostgresImportCommitReceipt.LockDecisionAsync(fence, transaction, "honua", OperationId,
                JobExecutionLeaseIntentFingerprint.Compute(job), deadline.Token);
            resumeOriginal.SetResult();

            await using var observer = await _dataSource.OpenConnectionAsync(deadline.Token);
            await using var blocked = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND $1 = ANY(pg_blocking_pids(pid)))
                """, observer);
            blocked.Parameters.AddWithValue(fence.ProcessID);
            while (!(bool)(await blocked.ExecuteScalarAsync(deadline.Token))!)
            {
                Assert.False(importing.IsCompleted, "The original importer must reach the real decision lock.");
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
            // This check must happen after the decision lock, not while waiting.
            Assert.Equal(1, authority.Checks);
            await transaction.CommitSafelyAsync(deadline.Token);
            var result = await importing.WaitAsync(deadline.Token);
            Assert.False(result.Success);
            Assert.Equal(2, authority.Checks);
            Assert.Null(await ReadAsync(job));
            Assert.Equal(true, await ScalarAsync("SELECT to_regclass('honua_data.imported_roads') IS NULL"));
            Assert.Equal(true, await ScalarAsync("SELECT to_regclass('honua_data.imported_roads__staging') IS NULL"));
        }
        finally
        {
            await transaction.DisposeAsync();
            await deadline.CancelAsync();
            resumeOriginal.TrySetCanceled(deadline.Token);
            try { await importing; }
            catch (OperationCanceledException) { }
        }
    }

    [IntegrationTest]
    public async Task AuthorityRevokedAfterRename_RollsBackDestinationAndReceipt()
    {
        var job = CreateJob();
        var authority = new CommitAuthority(job)
        {
            BeforeCheck = check => check == 3
                ? Task.FromException(new UnauthorizedAccessException("Revoked before durable commit")) : Task.CompletedTask
        };
        using var scope = JobSecurityScope.BeginExecution(job, job.Audit.SubmitterSecurityContext!);
        var result = await ImportAsync(authority);
        result.Success.Should().BeFalse();
        authority.Checks.Should().Be(3);
        (await ScalarAsync("SELECT to_regclass('honua_data.imported_roads') IS NULL")).Should().Be(true);
        (await ScalarAsync("SELECT to_regclass('honua_data.imported_roads__staging') IS NULL")).Should().Be(true);
        (await ReadAsync(job)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task CompetingOwnedTableAfterPreflight_IsNeverReplaced()
    {
        var job = CreateJob();
        var authority = new CommitAuthority(job)
        {
            BeforeCheck = check => check == 2 ? ExecuteAsync("""
                CREATE TABLE honua_data.imported_roads (sentinel text);
                INSERT INTO honua_data.imported_roads VALUES ('keep');
                COMMENT ON TABLE honua_data.imported_roads IS 'honua:import-owned:v1';
                """) : Task.CompletedTask
        };
        using var scope = JobSecurityScope.BeginExecution(job, job.Audit.SubmitterSecurityContext!);
        var result = await ImportAsync(authority, ImportLoadMode.Replace);
        result.Success.Should().BeFalse();
        (await ScalarAsync("SELECT sentinel FROM honua_data.imported_roads")).Should().Be("keep");
        (await ReadAsync(job)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task ExistingReceipt_PreventsSecondCommitEvenWhenOriginalTableWasRemoved()
    {
        var job = CreateJob();
        var authority = new CommitAuthority(job);
        using var scope = JobSecurityScope.BeginExecution(job, job.Audit.SubmitterSecurityContext!);
        var original = await ImportAsync(authority);
        original.Success.Should().BeTrue(original.ErrorMessage);
        await ExecuteAsync("DROP TABLE honua_data.imported_roads");
        var retry = await ImportAsync(new CommitAuthority(job));
        retry.Success.Should().BeFalse();
        (await ScalarAsync("SELECT to_regclass('honua_data.imported_roads') IS NULL")).Should().Be(true);
        (await ReadAsync(job)).Should().BeEquivalentTo(original);
        (await ScalarAsync("SELECT count(*) FROM honua.import_commit_receipt")).Should().Be(1L);
    }

    [IntegrationTest]
    public async Task CompletionNotificationFailure_CannotReplaceCommittedOriginalResult()
    {
        var job = CreateJob();
        using var scope = JobSecurityScope.BeginExecution(job, job.Audit.SubmitterSecurityContext!);
        var result = await ImportAsync(new CommitAuthority(job), progress: new ThrowAtCompletion());
        result.Success.Should().BeTrue(result.ErrorMessage);
        (await ReadAsync(job)).Should().BeEquivalentTo(result);
        (await ScalarAsync("SELECT count(*) FROM honua_data.imported_roads")).Should().Be(2L);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedKmz_UsesProcessingScratchBeforeDestinationMutation(bool refuseScratch)
    {
        // Actual archive reader and database path; the Core scratch port is controlled.
        // Allocator disk/accounting ordering is covered by ImportProcessingScratchTests.
        var job = CreateJob();
        using var scope = JobSecurityScope.BeginExecution(job, job.Audit.SubmitterSecurityContext!);
        await using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open(), Encoding.UTF8))
            writer.Write("""
                <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark>
                <name>Harbor</name><Point><coordinates>-157.5,21.25</coordinates></Point>
                </Placemark></Document></kml>
                """);
        source.Position = 0;
        var transfer = new ImportTransferProvenance(new string('a', 32), "actor", null, "target", "workspace", "task", 1, new string('b', 64));
        var processing = new ControlledProcessingScratch(refuseScratch);
        var service = new StreamingFileImportService(_connections,
            new CrsDetectionService(_connections, NullLogger<CrsDetectionService>.Instance),
            new TestFileFormatDetectionService(), new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
            schemaConfiguration: _schemas, commitAuthority: new CommitAuthority(job), processingScratchFactory: processing);
        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = source,
            FileName = "roads.kmz",
            TableName = "roads",
            TargetSchema = "honua_data",
            SourceSrid = 4326,
            TargetSrid = 4326,
            StagedSource = new($"imports/resumable/{transfer.UploadId}/source-{new string('c', 32)}", "generation", source.Length,
                Convert.ToHexStringLower(SHA256.HashData(source.ToArray())), Transfer: transfer)
        });
        Assert.Equal(transfer, processing.Transfer);
        Assert.Equal(1, processing.Writes);
        Assert.False(Directory.Exists(processing.DirectoryPath));
        Assert.Equal(!refuseScratch, result.Success);
        if (refuseScratch)
        {
            Assert.Null(await ReadAsync(job));
            Assert.Equal(true, await ScalarAsync("SELECT to_regclass('honua_data.imported_roads') IS NULL"));
        }
        else
        {
            Assert.Equal(1, result.FeatureCount);
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM honua_data.imported_roads"));
            (await ReadAsync(job)).Should().BeEquivalentTo(result);
        }
    }

    private sealed class ControlledProcessingScratch(bool refuse) : IImportProcessingScratchFactory, IImportProcessingScratch
    {
        public string DirectoryPath { get; } = Path.Join(Path.GetTempPath(), "honua-kmz-port-" + Guid.NewGuid().ToString("N"));
        internal ImportTransferProvenance? Transfer { get; private set; }
        internal int Writes { get; private set; }
        public Task<IImportProcessingScratch> CreateAsync(ImportTransferProvenance transfer, long maximumBytes,
            int maximumFiles, CancellationToken cancellationToken = default)
        {
            Transfer = transfer;
            Directory.CreateDirectory(DirectoryPath);
            return Task.FromResult<IImportProcessingScratch>(this);
        }
        public async Task WriteFileAsync(Stream source, string path, long expectedBytes, CancellationToken cancellationToken = default)
        {
            Writes++;
            Assert.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, path);
            if (refuse) throw new InvalidOperationException("Controlled scratch budget exhausted.");
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(output, cancellationToken);
            Assert.Equal(expectedBytes, output.Length);
        }
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
            return ValueTask.CompletedTask;
        }
    }

    private async Task<ImportResult> ImportAsync(CommitAuthority authority,
        ImportLoadMode mode = ImportLoadMode.Append, IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var service = new StreamingFileImportService(_connections,
            new CrsDetectionService(_connections, NullLogger<CrsDetectionService>.Instance),
            new TestFileFormatDetectionService(), new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance, schemaConfiguration: _schemas, commitAuthority: authority);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(GeoJson));
        return await service.ImportFileAsync(new ImportRequest
        {
            FileStream = source,
            FileName = "roads.geojson",
            TableName = "roads",
            TargetSchema = "honua_data",
            SourceSrid = 4326,
            TargetSrid = 4326,
            LoadMode = mode,
            OverwriteExisting = mode == ImportLoadMode.Replace,
            KeyColumns = mode == ImportLoadMode.Upsert ? ["code"] : []
        }, progress, cancellationToken);
    }

    private Task<ImportResult?> ReadAsync(ExecutionJobRecord job) =>
        new PostgresImportCommittedResultReader(_connections, _schemas).TryReadAsync(OperationId,
            JobExecutionLeaseIntentFingerprint.Compute(job));

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static ExecutionJobRecord CreateJob()
    {
        var now = DateTimeOffset.UtcNow;
        var context = new JobSecurityContext("executor", null, [])
        {
            OwnerActorId = "executor",
            ExecutionLease = new()
            {
                LeaseId = "controlled-provider-test",
                JobId = OperationId,
                IssuedAt = now,
                ExpiresAt = now.AddMinutes(30),
                ProtectedAuthority = "controlled-Core-port-not-a-Host-proof"
            }
        };
        return new()
        {
            OperationId = OperationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new()
            {
                Kind = ExecutionJobKind.FileImport,
                TargetKind = BatchComputeTargetKind.LocalProcess,
                Backend = "local",
                WorkloadName = "file-import"
            },
            Audit = new() { RequestedBy = "executor", SubmitterSecurityContext = context }
        };
    }

    private sealed class CommitAuthority(ExecutionJobRecord job) : IImportExecutionCommitAuthority
    {
        public int Checks { get; private set; }
        public Func<int, Task>? BeforeCheck { get; init; }

        public async Task<ImportExecutionCommitBinding> RequireCurrentAsync(ImportRequest request, string physicalSchema,
            string physicalTable, CancellationToken cancellationToken = default)
        {
            physicalSchema.Should().Be("honua_data");
            physicalTable.Should().Be("imported_roads");
            request.TableName.Should().Be("roads");
            Checks++;
            if (BeforeCheck is not null) await BeforeCheck(Checks);
            cancellationToken.ThrowIfCancellationRequested();
            return new(OperationId, JobExecutionLeaseIntentFingerprint.Compute(job), true);
        }
    }

    private sealed class ThrowAtCompletion : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value)
        {
            if (value.Status == ImportStatus.Completed) throw new InvalidOperationException("Completion subscriber failed");
        }
    }
}
