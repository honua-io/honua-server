// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class InMemoryImportJobServiceCleanupTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task CleanupCompletedJobs_EnforcesMaxCompletedJobs()
    {
        var importService = new FakeFileImportService();
        var performanceMonitor = new NoopPerformanceMonitor();
        var logger = NullLogger<InMemoryImportJobService>.Instance;
        using var jobService = new InMemoryImportJobService(importService, performanceMonitor, logger);

        var maxCompletedJobs = GetMaxCompletedJobs();
        var totalJobs = maxCompletedJobs + 5;
        var jobIds = new List<string>(totalJobs);

        for (var i = 0; i < totalJobs; i++)
        {
            using var stream = new MemoryStream(new byte[] { 0x01 });
            var request = new ImportRequest
            {
                FileStream = stream,
                FileName = $"sample-{i}.geojson",
                TableName = $"import_{i}",
                TargetSrid = 4326
            };

            var jobId = await jobService.QueueImportAsync(request, fileSize: 1);
            jobIds.Add(jobId);
        }

        await WaitForCompletionAsync(jobService, TimeSpan.FromSeconds(30));
        await TriggerCleanupAsync(jobService);

        var remaining = GetJobCount(jobService);
        remaining.Should().BeLessOrEqualTo(maxCompletedJobs);
        remaining.Should().BeLessThan(totalJobs);

        var overflow = totalJobs - maxCompletedJobs;
        var removed = 0;
        for (var i = 0; i < overflow; i++)
        {
            if (await jobService.GetProgressAsync(jobIds[i]) == null)
            {
                removed++;
            }
        }

        var newest = await jobService.GetProgressAsync(jobIds[^1]);
        removed.Should().BeGreaterThan(0);
        newest.Should().NotBeNull();
    }

    [Fact]
    public async Task QueueImportAsync_WithLocalFilePath_DeletesStagedFileAfterCompletion()
    {
        var importService = new FakeFileImportService();
        var performanceMonitor = new NoopPerformanceMonitor();
        var logger = NullLogger<InMemoryImportJobService>.Instance;
        using var jobService = new InMemoryImportJobService(importService, performanceMonitor, logger);

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"honua-local-import-{Guid.NewGuid():N}.geojson");
        await File.WriteAllTextAsync(tempFilePath, "{\"type\":\"FeatureCollection\",\"features\":[]}");

        var request = new ImportRequest
        {
            LocalFilePath = tempFilePath,
            FileName = "local-file.geojson",
            TableName = "import_local",
            TargetSrid = 4326
        };

        _ = await jobService.QueueImportAsync(request, fileSize: new FileInfo(tempFilePath).Length);
        await WaitForCompletionAsync(jobService, TimeSpan.FromSeconds(30));

        File.Exists(tempFilePath).Should().BeFalse();
    }

    private static async Task WaitForCompletionAsync(InMemoryImportJobService jobService, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var activeJobs = await jobService.GetActiveJobsAsync();
            if (activeJobs.Count == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("Timed out waiting for import jobs to complete.");
    }

    private static int GetMaxCompletedJobs()
    {
        var field = typeof(InMemoryImportJobService)
            .GetField("MaxCompletedJobs", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull();
        return (int)(field?.GetRawConstantValue() ?? 0);
    }

    private static int GetJobCount(InMemoryImportJobService jobService)
    {
        var field = typeof(InMemoryImportJobService)
            .GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull();
        var jobs = field?.GetValue(jobService);
        jobs.Should().NotBeNull();
        var countProperty = jobs?.GetType().GetProperty("Count");
        countProperty.Should().NotBeNull();
        return (int)(countProperty?.GetValue(jobs) ?? 0);
    }

    private static async Task TriggerCleanupAsync(InMemoryImportJobService jobService)
    {
        var type = typeof(InMemoryImportJobService);
        var intervalField = type.GetField("_cleanupInterval", BindingFlags.NonPublic | BindingFlags.Static);
        intervalField.Should().NotBeNull();
        var interval = (TimeSpan)(intervalField?.GetValue(null) ?? TimeSpan.Zero);

        var lastCleanupField = type.GetField("_lastCleanupTick", BindingFlags.NonPublic | BindingFlags.Instance);
        lastCleanupField.Should().NotBeNull();
        lastCleanupField?.SetValue(jobService, Environment.TickCount64 - (long)interval.TotalMilliseconds - 1);

        _ = await jobService.GetProgressAsync("force-cleanup");
    }

    private sealed class FakeFileImportService : IFileImportService
    {
        public ImportLimits Limits { get; } = new();

        public SupportedFileFormat? DetectFormat(string fileName)
        {
            return SupportedFileFormat.GeoJson;
        }

        public string[] GetSupportedExtensions()
        {
            return [".geojson"];
        }

        public Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default)
        {
            return ImportFileAsync(request, progress: null, cancellationToken);
        }

        public async Task<ImportResult> ImportFileAsync(ImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default)
        {
            if (TryGetIndex(request.TableName, out var index))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(index), cancellationToken);
            }

            var result = ImportResult.CreateSuccess(request.TableName, SupportedFileFormat.GeoJson, 1);
            return result;
        }

        public Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
        {
            var preview = new FilePreview
            {
                Format = SupportedFileFormat.GeoJson,
                TotalFeatureCount = 0
            };
            return Task.FromResult(preview);
        }

        private static bool TryGetIndex(string tableName, out int index)
        {
            const string prefix = "import_";
            if (tableName.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(tableName.AsSpan(prefix.Length), out var parsed))
            {
                index = parsed;
                return true;
            }

            index = 0;
            return false;
        }
    }

}
