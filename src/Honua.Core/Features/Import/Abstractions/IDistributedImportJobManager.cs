// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Combined interface for distributed import job management.
/// </summary>
public interface IDistributedImportJobManager
{
    /// <summary>
    /// Job queue for Geoservices imports.
    /// </summary>
    IDistributedJobQueueService JobQueue { get; }

    /// <summary>
    /// Leader election for background processing.
    /// </summary>
    IDistributedLeaderElection LeaderElection { get; }

    /// <summary>
    /// Progress store for tracking import jobs.
    /// </summary>
    IDistributedProgressStore<GeoservicesImportProgress> ProgressStore { get; }

    /// <summary>
    /// Store for import requests (needed by background worker).
    /// </summary>
    IDistributedProgressStore<GeoservicesImportRequest> RequestStore { get; }
}
