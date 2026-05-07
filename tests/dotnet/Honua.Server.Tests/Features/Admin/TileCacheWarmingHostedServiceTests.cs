// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Cache)]
public sealed class TileCacheWarmingHostedServiceTests
{
    [UnitTest]
    public async Task StartAsync_WhenEnabled_QueuesConfiguredSeedAndWarmTargets()
    {
        var jobService = new RecordingTileOperationJobService();
        var options = Options.Create(new TileCacheWarmingOptions
        {
            Enabled = true,
            Targets =
            [
                new TileCacheWarmingTarget
                {
                    Operation = "seed",
                    ServiceId = "parks",
                    LayerId = 3,
                    MinZoom = 4,
                    MaxZoom = 8,
                    TileMatrixSetId = "WebMercatorQuad",
                    MaxTiles = 50
                },
                new TileCacheWarmingTarget
                {
                    Operation = "warm",
                    ServiceId = "roads",
                    MinZoom = 0,
                    MaxZoom = 2
                }
            ]
        });
        var service = new TileCacheWarmingHostedService(
            jobService,
            options,
            NullLogger<TileCacheWarmingHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        jobService.Requests.Should().HaveCount(2);
        jobService.Requests[0].Operation.Should().Be("seed");
        jobService.Requests[0].ServiceId.Should().Be("parks");
        jobService.Requests[0].LayerId.Should().Be(3);
        jobService.Requests[0].MinZoom.Should().Be(4);
        jobService.Requests[0].MaxZoom.Should().Be(8);
        jobService.Requests[1].Operation.Should().Be("warm");
        jobService.Requests[1].ServiceId.Should().Be("roads");
    }

    [UnitTest]
    public async Task StartAsync_WhenCommunityDefaultDisabled_DoesNotQueueJobs()
    {
        var jobService = new RecordingTileOperationJobService();
        var service = new TileCacheWarmingHostedService(
            jobService,
            Options.Create(new TileCacheWarmingOptions()),
            NullLogger<TileCacheWarmingHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        jobService.Requests.Should().BeEmpty();
    }

    private sealed class RecordingTileOperationJobService : ITileOperationJobService
    {
        public List<TileOperationStartRequest> Requests { get; } = [];

        public Task<string> StartAsync(TileOperationStartRequest request, string? schemaName = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult($"job-{Requests.Count}");
        }

        public Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult<TileOperationProgress?>(null);

        public Task<IReadOnlyList<TileOperationProgress>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TileOperationProgress>>([]);

        public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<string?> RetryAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public async IAsyncEnumerable<string> ReadQueuedJobIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task ProcessQueuedJobAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
