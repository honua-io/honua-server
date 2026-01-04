// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

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

    private sealed class NoopPerformanceMonitor : IPerformanceMonitor
    {
        public void RecordDatabaseQuery(string queryType, string layerId, TimeSpan duration, int recordCount)
        {
        }

        public void RecordHttpRequest(string method, string endpoint, int statusCode, TimeSpan duration)
        {
        }

        public void RecordActiveHttpRequestDelta(int delta)
        {
        }

        public void RecordMemoryUsage(long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections)
        {
        }

        public void RecordCacheMetrics(string cacheType, string operation)
        {
        }

        public void RecordTransactionDuration(TimeSpan duration, int operationCount, bool wasCommitted)
        {
        }

        public IOperationScope StartOperation(string operationName)
        {
            return new NoopOperationScope();
        }

        public void RecordCounter(string name, long value, IDictionary<string, string>? tags = null)
        {
        }

        public void RecordHistogram(string name, double value, IDictionary<string, string>? tags = null)
        {
        }
    }

    private sealed class NoopOperationScope : IOperationScope
    {
        public IOperationScope WithTag(string key, string value)
        {
            return this;
        }

        public void Dispose()
        {
        }
    }
}
