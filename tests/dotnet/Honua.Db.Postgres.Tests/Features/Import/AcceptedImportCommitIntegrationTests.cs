// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
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
            "129_ProtectImportTableOwnership.sql", "136_CreateImportCommitReceipts.sql" })
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

    private async Task<ImportResult> ImportAsync(CommitAuthority authority,
        ImportLoadMode mode = ImportLoadMode.Append, IProgress<ImportProgress>? progress = null)
    {
        var service = new StreamingFileImportService(_connections,
            new CrsDetectionService(_connections, NullLogger<CrsDetectionService>.Instance),
            new TestFileFormatDetectionService(), new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance, schemaConfiguration: _schemas, commitAuthority: authority);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(GeoJson));
        return await service.ImportFileAsync(new ImportRequest
        {
            FileStream = source, FileName = "roads.geojson", TableName = "roads", TargetSchema = "honua_data",
            SourceSrid = 4326, TargetSrid = 4326, LoadMode = mode,
            OverwriteExisting = mode == ImportLoadMode.Replace, KeyColumns = mode == ImportLoadMode.Upsert ? ["code"] : []
        }, progress);
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
            ExecutionLease = new() { LeaseId = "controlled-provider-test", JobId = OperationId,
                IssuedAt = now, ExpiresAt = now.AddMinutes(30), ProtectedAuthority = "controlled-Core-port-not-a-Host-proof" }
        };
        return new()
        {
            OperationId = OperationId, Status = ExecutionJobStatus.Running, CreatedAt = now, UpdatedAt = now,
            Spec = new() { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.LocalProcess,
                Backend = "local", WorkloadName = "file-import" },
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
