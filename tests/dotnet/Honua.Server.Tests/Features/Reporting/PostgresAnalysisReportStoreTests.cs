// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Domain;
using Honua.Server.Features.Reporting;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Features.Reporting;

[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Metadata)]
public sealed class PostgresAnalysisReportStoreTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private readonly string _schema = "analysis_reports_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        await database.CreateSchemaUnderLockAsync(_schema);
        await using var stream = typeof(PostgresAnalysisReportStore).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.177_CreateAnalysisReports.sql")
            ?? throw new InvalidOperationException("Analysis report migration is missing.");
        using var reader = new StreamReader(stream);
        await database.ExecuteDdlUnderLockAsync((await reader.ReadToEndAsync())
            .Replace("$HonuaSchema$", _schema, StringComparison.Ordinal));
    }

    public Task DisposeAsync() => database.DropSchemaAsync(_schema);

    [IntegrationTest]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}")]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task StoreAsync_RecreatedPool_RoundTripsAllSectionTypes()
    {
        var report = Report("job|one");
        await using (var pool = NpgsqlDataSource.Create(database.ConnectionString))
        {
            var writer = new PostgresAnalysisReportStore(new JobStoreDatabase(pool, _schema), OptionsFor(16));
            await writer.StoreAsync(report, CancellationToken.None);
        }
        (await Store().TryGetAsync(report.JobId, report.ReportContractVersion, CancellationToken.None))
            .Should().BeEquivalentTo(report, options => options.RespectingRuntimeTypes());
        (await Store().TryGetAsync(report.JobId, "different-version", CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}")]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task RemoveAsync_FromAnotherNode_InvalidatesEveryVersionOnlyForThatJob()
    {
        var first = Store();
        await first.StoreAsync(Report("job-A"), CancellationToken.None);
        await first.StoreAsync(Report("job-A") with { ReportContractVersion = "future" }, CancellationToken.None);
        await first.StoreAsync(Report("job-B"), CancellationToken.None);
        await Store().RemoveAsync("job-A", CancellationToken.None);
        (await first.TryGetAsync("job-A", ReportingConstants.ContractVersionV1, CancellationToken.None)).Should().BeNull();
        (await first.TryGetAsync("job-A", "future", CancellationToken.None)).Should().BeNull();
        (await first.TryGetAsync("job-B", ReportingConstants.ContractVersionV1, CancellationToken.None)).Should().NotBeNull();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}")]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task TryGetAsync_ExpiredReport_IsAMissAndReplacementRefreshesExpiry()
    {
        var store = Store();
        await store.StoreAsync(Report("expired"), CancellationToken.None);
        await using var command = database.DataSource.CreateCommand(
            $"UPDATE \"{_schema}\".analysis_reports SET expires_at = CURRENT_TIMESTAMP - INTERVAL '1 second'");
        await command.ExecuteNonQueryAsync();
        (await store.TryGetAsync("expired", ReportingConstants.ContractVersionV1, CancellationToken.None)).Should().BeNull();
        var replacement = Report("expired") with { ResultPackageId = "replacement" };
        await store.StoreAsync(replacement, CancellationToken.None);
        (await Store().TryGetAsync("expired", ReportingConstants.ContractVersionV1, CancellationToken.None))
            .Should().BeEquivalentTo(replacement, options => options.RespectingRuntimeTypes());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}")]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task StoreAsync_ConcurrentNodes_RespectSharedCapacity()
    {
        var first = Store(2);
        var second = Store(2);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            (index % 2 == 0 ? first : second).StoreAsync(Report($"job-{index}"), CancellationToken.None)));
        await using var command = database.DataSource.CreateCommand($"SELECT count(*) FROM \"{_schema}\".analysis_reports");
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(2);
    }

    private PostgresAnalysisReportStore Store(int maxEntries = 16)
        => new(new JobStoreDatabase(database.DataSource, _schema), OptionsFor(maxEntries));

    private static IOptions<ReportingConfiguration> OptionsFor(int maxEntries)
        => Options.Create(new ReportingConfiguration
        {
            Cache = new ReportingCacheConfiguration { MaxEntries = maxEntries, TtlMinutes = 60 },
        });

    private static AnalysisReport Report(string jobId) => new()
    {
        ReportId = $"{jobId}|package",
        ReportContractVersion = ReportingConstants.ContractVersionV1,
        JobId = jobId,
        ResultPackageId = "package",
        ProcessId = "analytics.buffer-aggregate",
        ProcessFamily = "analytics",
        TemplateId = "analysis-report.analytics-buffer-aggregate",
        TemplateVersion = "1.0.0",
        Summary = new ResultSummary { Title = "T", Description = "D" },
        Sections =
        [
            new HeadingSection { Text = "Heading", Level = 2 },
            new ParagraphSection { Text = "Quoted \"text\"" },
            new KeyMetricSection { Label = "Area", Value = "12.5", Unit = "m2", SpatialReference = "EPSG:4326" },
            new TableSection { Caption = "Table", Columns = ["value"], Rows = [["one"]], TruncatedRowCount = 2 },
            new ChartSection { ChartKind = ReportChartKind.Bar, Categories = ["one"], Series = [new ChartSeries { Name = "count", Values = [1.5] }] },
            new MapEmbedSection { Caption = "Map", MapPackageUri = "https://example.test/map" },
            new NarrativeSection { SlotId = "summary", DeterministicText = "Summary", LlmText = "Narrative", Mode = NarrativeMode.Deterministic },
            new ProvenanceFooterSection { ResultPackageId = "package", JobId = jobId, ProcessDefinitions = ["process"], Sources = ["source"], ExecutedAt = DateTimeOffset.UnixEpoch, GeneratedAt = DateTimeOffset.UnixEpoch },
        ],
        NarrativeMode = NarrativeMode.Deterministic,
        Provenance = new ProvenanceRecord
        {
            Sources = [],
            ProcessDefinitions = ["analytics.buffer-aggregate"],
            ExecutedAt = DateTimeOffset.UnixEpoch,
            GeneratedArtifactIds = ["package"],
        },
        Assumptions = ["sample"],
        GeneratedAt = DateTimeOffset.Parse("2026-11-14T01:02:03.1234567+02:00", CultureInfo.InvariantCulture),
    };
}
