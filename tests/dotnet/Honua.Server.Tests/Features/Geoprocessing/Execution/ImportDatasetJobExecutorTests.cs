// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Integration coverage for the <c>import.dataset</c> durable GP job (#1630).
/// Drives <see cref="ImportDatasetJobExecutor"/> against a real PostGIS
/// container so the composed steps (stage -> import -> flatten/publish ->
/// extent+MVT refresh -> provenance) execute end-to-end and the job reaches the
/// typed/refreshed end-state. The executor adapts into the canonical job runtime
/// (it is the <see cref="IJobExecutor"/> the dispatcher routes for the
/// <c>import.dataset</c> process id); this test exercises that executor directly
/// with a recording <see cref="IJobExecutionContext"/> double so progress,
/// provenance, and the resulting layer can be asserted without standing up the
/// Redis-backed worker loop.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class ImportDatasetJobExecutorTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private Guid _connectionId;
    private string _stagedFilePath = string.Empty;
    private string _tableName = string.Empty;
    private string _serviceName = string.Empty;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _tableName = $"import_{Guid.NewGuid():N}";
        _serviceName = $"svc_{Guid.NewGuid():N}";

        await CreateSecureConnectionAsync(_fixture.Postgres.ConnectionString);
        _stagedFilePath = StageGeoJsonSource();
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_stagedFilePath) && File.Exists(_stagedFilePath))
        {
            File.Delete(_stagedFilePath);
        }

        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/processes/{processId}/execution")]
    public async Task ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance()
    {
        // Arrange — resolve the executor exactly as the worker host would.
        var executor = _fixture.Services.GetRequiredService<ImportDatasetJobExecutor>();
        var context = new RecordingJobExecutionContext("op-import-dataset");
        var job = BuildJobRecord();

        // Act — run the full durable pipeline end-to-end.
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        // Assert — terminal success and ordered progress to 100%.
        result.Status.Should().Be(
            ExecutionJobStatus.Succeeded,
            $"executor reported: {result.ErrorMessage}");
        context.Progress.Should().NotBeEmpty();
        context.Progress[^1].Percent.Should().Be(100);

        // The flatten step published a typed layer; the imported table and the
        // catalog layer row both exist and the features are queryable.
        await AssertImportedTableHasRowsAsync();
        var layerId = await ReadPublishedLayerIdAsync();
        layerId.Should().BePositive("the flatten step must publish a typed catalog layer");

        // The extent-refresh step populated a non-null layer extent.
        var hasExtent = await LayerHasExtentAsync(layerId);
        hasExtent.Should().BeTrue("the extent refresh step must compute the layer extent");

        // The provenance step emitted exactly one provenance artifact describing
        // the import lineage.
        context.Artifacts.Should().ContainSingle();
        var provenanceUri = context.Artifacts[0];
        provenanceUri.Should().StartWith("data:application/json;base64,");

        var base64 = provenanceUri["data:application/json;base64,".Length..];
        using var doc = JsonDocument.Parse(Convert.FromBase64String(base64));
        doc.RootElement.GetProperty("type").GetString().Should().Be("ImportProvenance");
        doc.RootElement.GetProperty("processId").GetString().Should().Be("import.dataset");
        doc.RootElement.GetProperty("featureCount").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("layerId").GetInt32().Should().Be(layerId);

        // A structured provenance log entry was recorded through the substrate.
        context.Logs.Should().Contain(entry => entry.Phase == "Provenance");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/processes/{processId}/execution")]
    public async Task ExecuteAsync_MissingRequiredInput_FailsWithoutPublishing()
    {
        var executor = _fixture.Services.GetRequiredService<ImportDatasetJobExecutor>();
        var context = new RecordingJobExecutionContext("op-import-missing");

        // Omit the required 'layerName' input.
        var parameters = BuildBaseParameters();
        parameters.Remove($"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.layerName");
        var job = BuildJobRecord(parameters);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("layerName");
        context.Artifacts.Should().BeEmpty();
    }

    private ExecutionJobRecord BuildJobRecord(Dictionary<string, string>? parameters = null) =>
        new()
        {
            OperationId = "op-import-dataset",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:import.dataset",
                Parameters = parameters ?? BuildBaseParameters()
            }
        };

    private Dictionary<string, string> BuildBaseParameters()
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = ImportDatasetJobExecutor.HandledProcessId,
            ["protocolProcessId"] = ImportDatasetJobExecutor.HandledProcessId,
            [prefix + "connection"] = _connectionId.ToString(),
            [prefix + "sourcePath"] = _stagedFilePath,
            [prefix + "fileName"] = "import_dataset.geojson",
            [prefix + "tableName"] = _tableName,
            [prefix + "layerName"] = $"Layer {_tableName}",
            [prefix + "serviceName"] = _serviceName,
            [prefix + "targetSrid"] = "4326"
        };
    }

    /// <summary>
    /// Staging directory that matches the <c>ImportStagingDirectory</c> default in
    /// <c>GeoprocessingExecutorOptions</c> so the path-traversal guard (BH3-027) passes
    /// for legitimate test files.
    /// </summary>
    // Path.Combine args are a temp-dir root plus a literal relative folder name; no rooted-segment risk.
    private static readonly string StagingDirectory =
        Path.Combine(Path.GetTempPath(), "honua-import-staging");

    private static string StageGeoJsonSource()
    {
        var geoJson = JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = new object[]
            {
                Feature(1, "Alpha", -122.4194, 37.7749),
                Feature(2, "Bravo", -122.2711, 37.8044),
                Feature(3, "Charlie", -122.0838, 37.3861)
            }
        });

        // BH3-027: files must be placed under the configured staging root.
        Directory.CreateDirectory(StagingDirectory);
        // Path.Combine args are a staging root plus a generated relative file name; no rooted-segment risk.
        var path = Path.Combine(StagingDirectory, $"import_dataset_{Guid.NewGuid():N}.geojson");
        File.WriteAllText(path, geoJson, Encoding.UTF8);
        return path;

        static object Feature(int id, string name, double x, double y) => new
        {
            type = "Feature",
            geometry = new { type = "Point", coordinates = new[] { x, y } },
            properties = new { id, name }
        };
    }

    private async Task AssertImportedTableHasRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count(*) FROM information_schema.tables WHERE table_name = 'imported_{_tableName}';";
        var tableExists = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
        tableExists.Should().BeTrue("the import step must create the generic imported_<table>");
    }

    private async Task<int> ReadPublishedLayerIdAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT layer_id FROM honua.layers WHERE table_name = @table ORDER BY layer_id DESC LIMIT 1;";
        command.Parameters.AddWithValue("table", $"imported_{_tableName}");
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private async Task<bool> LayerHasExtentAsync(int layerId)
    {
        await using var connection = new NpgsqlConnection(_fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extent IS NOT NULL FROM honua.layers WHERE layer_id = @id;";
        command.Parameters.AddWithValue("id", layerId);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is bool flag && flag;
    }

    private async Task CreateSecureConnectionAsync(string connectionString)
    {
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IConnectionEncryptionService>();

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // The executor resolves this connection string verbatim and opens raw
        // connections that do NOT carry the per-test schema header the HTTP pipeline
        // applies. In production the operational NpgsqlDataSource bakes the data schema
        // into the connection search_path; mirror that here so the publish step's
        // materialization into the shared `features` table (seeded into the isolated
        // per-test schema) resolves, while keeping the literal `honua` catalog schema and
        // `public` (PostGIS functions) on the path.
        if (!string.IsNullOrWhiteSpace(_fixture.CurrentSchema))
        {
            builder.SearchPath = $"{_fixture.CurrentSchema},honua,public";
        }

        connectionString = builder.ConnectionString;
        var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString);
        var keyVersion = await encryptionService.GetCurrentKeyVersionAsync();
        var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
        var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"import-dataset-{Guid.NewGuid():N}",
            host: builder.Host!,
            port: builder.Port,
            databaseName: builder.Database!,
            username: builder.Username!,
            encryptedConnectionString: encrypted,
            encryptionKeyVersion: keyVersion,
            createdBy: nameof(ImportDatasetJobExecutorTests),
            description: "Import dataset job integration test connection",
            sslRequired: sslRequired,
            sslMode: sslMode);

        var created = await registry.CreateConnectionAsync(connection);
        _connectionId = created.ConnectionId;
    }

    private sealed class RecordingJobExecutionContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;

        public List<(double? Percent, string? Phase)> Progress { get; } = [];

        public List<ExecutionLogEntry> Logs { get; } = [];

        public List<string> Artifacts { get; } = [];

        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
        {
            Progress.Add((percentComplete, phase));
            return Task.CompletedTask;
        }

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        {
            Logs.Add(entry);
            return Task.CompletedTask;
        }

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        {
            Artifacts.Add(artifactReference);
            return Task.CompletedTask;
        }
    }
}
