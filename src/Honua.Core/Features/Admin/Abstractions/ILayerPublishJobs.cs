// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

/// <summary>
/// Accepts a layer publish. At or under the row threshold the publish runs to completion on the
/// publication budget, not on <see cref="CancellationToken"/> from the request. Above the threshold
/// it becomes a pollable job on the shared execution-job queue, or an in-process task when that
/// queue is absent. A frozen host requires a remote durable worker.
/// </summary>
public interface ILayerPublishJobs
{
    /// <summary>Publishes a layer or accepts it for background execution.</summary>
    /// <param name="connectionString">Resolved source connection, never persisted in a job.</param>
    /// <param name="connectionId">The registered connection identifier, if any.</param>
    /// <param name="request">The validated domain publication request.</param>
    /// <param name="routeId">The connection route identifier used for polling.</param>
    /// <param name="requestCancellation">Cancellation before admission; accepted work is detached.</param>
    Task<LayerPublishAdmission> SubmitAsync(
        string connectionString,
        Guid connectionId,
        LayerPublishRequest request,
        string routeId,
        CancellationToken requestCancellation);

    /// <summary>Reads publication progress visible to the current tenant.</summary>
    /// <param name="jobId">The publication job identifier.</param>
    /// <param name="cancellationToken">Cancellation for the read.</param>
    Task<LayerPublishJobProgress?> GetAsync(string jobId, CancellationToken cancellationToken);
}
