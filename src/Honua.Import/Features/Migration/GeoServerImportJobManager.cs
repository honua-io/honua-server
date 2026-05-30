// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Server.Features.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Server.Features.Migration;

/// <summary>
/// Redis-backed coordination for GeoServer import jobs with in-memory fallback in development and tests.
/// </summary>
internal sealed class GeoServerImportJobManager : IImportWorkerJobManager<GeoServerImportRequest, GeoServerImportProgress>, IDisposable
{
    private readonly ImportJobManagerState<GeoServerImportRequest, GeoServerImportProgress> _state;

    public GeoServerImportJobManager(
        IUniversalProgressStore universalProgressStore,
        IDistributedCache? distributedCache,
        ILogger<GeoServerImportJobManager> logger,
        IHostEnvironment hostEnvironment,
        IConnectionMultiplexer? redis = null)
    {
        _state = new ImportJobManagerState<GeoServerImportRequest, GeoServerImportProgress>(
            universalProgressStore,
            distributedCache,
            logger,
            hostEnvironment,
            redis,
            "geoserver:import:queue",
            "geoserver:import:leader",
            "geoserver:import:request:",
            GeoServerImportApiJsonContext.Default.GeoServerImportRequest);
    }

    public bool CanAcceptNewJobs => _state.CanAcceptNewJobs;

    internal IDistributedJobQueueService JobQueue => _state.JobQueue;
    internal IDistributedLeaderElection LeaderElection => _state.LeaderElection;
    internal IDistributedProgressStore<GeoServerImportProgress> ProgressStore => _state.ProgressStore;
    internal IDistributedProgressStore<GeoServerImportRequest> RequestStore => _state.RequestStore;
    internal bool IsClusterDurable => _state.IsClusterDurable;

    IDistributedJobQueueService IImportWorkerJobManager<GeoServerImportRequest, GeoServerImportProgress>.JobQueue => JobQueue;
    IDistributedLeaderElection IImportWorkerJobManager<GeoServerImportRequest, GeoServerImportProgress>.LeaderElection => LeaderElection;
    IDistributedProgressStore<GeoServerImportProgress> IImportWorkerJobManager<GeoServerImportRequest, GeoServerImportProgress>.ProgressStore => ProgressStore;
    IDistributedProgressStore<GeoServerImportRequest> IImportWorkerJobManager<GeoServerImportRequest, GeoServerImportProgress>.RequestStore => RequestStore;

    public void Dispose()
    {
        if (_state.LeaderElection is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
