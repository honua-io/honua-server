// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.IO.Compression;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Migration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed class ImportLimitBehaviorTests
{
    [Fact]
    public void EnsureArchiveEntryCount_RejectsArchivesOverTheCap()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 3; i++)
            {
                using var entry = archive.CreateEntry($"e{i}").Open();
            }
        }

        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var act = () => StreamingFileImportService.EnsureArchiveEntryCount(zip, 2);
        act.Should().Throw<InvalidDataException>();
        StreamingFileImportService.EnsureArchiveEntryCount(zip, 10);
    }

    [Fact]
    public async Task ImportFeaturesAsync_UnmatchedFeatureType_FailsBeforeDatabase()
    {
        var scanner = new RecordingScanner(BuildInventory("demo:cities"));
        var service = new OgcWfsImportService(
            scanner,
            new ThrowingConnections(),
            new HttpClient(new HttpClientHandler()),
            NullLogger<OgcWfsImportService>.Instance,
            new PostgresSchemaConfiguration("honua", "honua_data", ["honua_data"]));

        var result = await service.ImportFeaturesAsync(new OgcWfsImportRequest
        {
            ServiceUrl = "https://gs.example/geoserver/wfs",
            Username = "u",
            Password = "p",
            FeatureTypeNames = ["secure:parcels"],
            DryRun = true,
            AllowUnsafeLocalUrls = true
        });

        result.Success.Should().BeFalse();
        result.Warnings.Should().Contain(warning => warning.Contains("secure:parcels", StringComparison.Ordinal));
        scanner.LastRequest!.Username.Should().Be("u");
        scanner.LastRequest.Password.Should().Be("p");
    }

    [Fact]
    public async Task ImportFeaturesAsync_PartialFeatureTypeMatch_WarnsAndContinues()
    {
        var scanner = new RecordingScanner(BuildInventory("demo:cities"));
        var service = new OgcWfsImportService(
            scanner,
            new ThrowingConnections(),
            new HttpClient(new HttpClientHandler()),
            NullLogger<OgcWfsImportService>.Instance,
            new PostgresSchemaConfiguration("honua", "honua_data", ["honua_data"]));

        var result = await service.ImportFeaturesAsync(new OgcWfsImportRequest
        {
            ServiceUrl = "https://gs.example/geoserver/wfs",
            FeatureTypeNames = ["demo:cities", "secure:parcels"],
            DryRun = true,
            AllowUnsafeLocalUrls = true
        });

        result.Success.Should().BeTrue();
        result.FeatureTypesPlanned.Should().Be(1);
        result.Warnings.Should().Contain(warning => warning.Contains("secure:parcels", StringComparison.Ordinal));
    }

    private static MigrationSourceInventoryArtifact BuildInventory(string featureTypeName)
        => new()
        {
            SourceKind = "ogc-wfs",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Demo WFS",
                BaseUrl = "https://gs.example/geoserver/wfs",
                ServiceType = "WFS",
                Version = "2.0.0"
            },
            AuthPosture = new MigrationInventoryAuthPosture { Mode = "basic" },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary { ResourceCount = 1 },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "ready"
            },
            Resources =
            [
                new MigrationInventoryResource
                {
                    Id = "feature-type:demo",
                    ContainerId = "namespace:demo",
                    Kind = "feature-type",
                    Name = featureTypeName,
                    GeometryType = "Point",
                    Compatibility = new MigrationCompatibilityAssessment
                    {
                        Level = "compatible",
                        Reason = "ready"
                    }
                }
            ]
        };

    private sealed class RecordingScanner(MigrationSourceInventoryArtifact inventory) : IOgcServiceMigrationScanner
    {
        public OgcServiceScanRequest? LastRequest { get; private set; }

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            OgcServiceScanRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(inventory);
        }
    }

    private sealed class ThrowingConnections : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => throw new InvalidOperationException("database should not be opened");

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("database should not be opened");

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("database should not be opened");

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("database should not be opened");

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("database should not be opened");
    }
}
