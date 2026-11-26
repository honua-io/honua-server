// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming.Conformance;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class FeatureStreamConformanceSweepServiceTests
{
    [UnitTest]
    public async Task ExecuteAsync_InternalTaskCanceledException_LogsErrorAndContinuesSubsequentSweeps()
    {
        var graphProvider = new TimeoutThenEmptyGraphProvider();

        var options = Options.Create(new FeatureStreamConformanceOptions
        {
            Enabled = true,
            ServiceId = "test-service",
            LayerId = 0,
            SweepInterval = TimeSpan.FromMilliseconds(20),
        });

        var store = new FeatureStreamConformanceStore(
            graphProvider,
            Substitute.For<IFeatureReader>(),
            Substitute.For<IFeatureWriter>(),
            Substitute.For<IGeometryService>());

        var conformanceService = new FeatureStreamConformanceService(
            options,
            new FeatureStreamConformanceRunRegistry(options),
            store,
            new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()),
            new DeploymentIdentity(new ConfigurationBuilder().Build()),
            NullLogger<FeatureStreamConformanceService>.Instance);

        await using var services = new ServiceCollection()
            .AddScoped(_ => conformanceService)
            .BuildServiceProvider();

        var logger = new ListLogger<FeatureStreamConformanceSweepService>();
        using var sweepService = new FeatureStreamConformanceSweepService(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            logger);

        await sweepService.StartAsync(CancellationToken.None);
        try
        {
            await graphProvider.SecondSweepReached.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await sweepService.StopAsync(CancellationToken.None);
        }

        graphProvider.CallCount.Should().BeGreaterThanOrEqualTo(2);
        logger.Entries.Should().Contain(entry =>
            entry.LogLevel == LogLevel.Error
            && entry.EventId.Id == 5147
            && entry.Exception is TaskCanceledException);
    }

    private sealed class TimeoutThenEmptyGraphProvider : IMetadataV2GraphProvider
    {
        private readonly TaskCompletionSource _secondSweepReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task SecondSweepReached => _secondSweepReached.Task;

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _callCount);
            if (attempt == 1)
            {
                throw new TaskCanceledException("Simulated internal database timeout");
            }

            _secondSweepReached.TrySetResult();
            return ValueTask.FromResult(new MetadataV2GraphSnapshot(new MetadataV2Graph(), "etag-1", DateTimeOffset.UtcNow));
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add(new LogEntry(logLevel, eventId, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, EventId EventId, Exception? Exception);
}
