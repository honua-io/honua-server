// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Infrastructure.Progress;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Cache)]
public sealed class TileCacheExpiryHostedServiceTests
{
    [UnitTest]
    public async Task SweepAsync_WhenEnabled_QueuesInvalidateJobForEachTarget()
    {
        var jobService = new RecordingTileOperationJobService();
        var options = Options.Create(new TileCacheExpiryOptions
        {
            Enabled = true,
            IntervalSeconds = 3600,
            Targets =
            [
                new TileCacheExpiryTarget
                {
                    ServiceId = "parks",
                    LayerId = 3,
                    TileMatrixSetId = "WebMercatorQuad"
                },
                new TileCacheExpiryTarget
                {
                    ServiceId = "roads"
                }
            ]
        });
        var service = new TileCacheExpiryHostedService(
            jobService,
            options,
            NullLogger<TileCacheExpiryHostedService>.Instance);

        await service.SweepAsync(CancellationToken.None);

        jobService.Requests.Should().HaveCount(2);
        jobService.Requests.Should().OnlyContain(request => request.Operation == "invalidate");
        jobService.Requests[0].ServiceId.Should().Be("parks");
        jobService.Requests[0].LayerId.Should().Be(3);
        jobService.Requests[1].ServiceId.Should().Be("roads");
    }

    [UnitTest]
    public async Task SweepAsync_SkipsTargetWithoutServiceOrLayer()
    {
        var jobService = new RecordingTileOperationJobService();
        var options = Options.Create(new TileCacheExpiryOptions
        {
            Enabled = true,
            Targets =
            [
                new TileCacheExpiryTarget { TileMatrixSetId = "WebMercatorQuad" },
                new TileCacheExpiryTarget { LayerId = 7 }
            ]
        });
        var service = new TileCacheExpiryHostedService(
            jobService,
            options,
            NullLogger<TileCacheExpiryHostedService>.Instance);

        await service.SweepAsync(CancellationToken.None);

        jobService.Requests.Should().ContainSingle();
        jobService.Requests[0].LayerId.Should().Be(7);
    }

    [UnitTest]
    public async Task ExecuteAsync_WhenDisabled_QueuesNothing()
    {
        var jobService = new RecordingTileOperationJobService();
        var service = new TileCacheExpiryHostedService(
            jobService,
            Options.Create(new TileCacheExpiryOptions()),
            NullLogger<TileCacheExpiryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

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
