// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Canonical async wrapper for branch-version reconcile/post (#1553). Starts a durable, pollable job
/// that runs the reconcile/post under the (service, version) version lock, persists its lifecycle and
/// outcome to the <see cref="IVersionJobStore"/>, and emits telemetry. Protocol adapters call this for
/// the async surface; the synchronous fast path on <see cref="IVersionManager"/> remains for small/fast
/// versions or when async is not requested. The runner never touches the DEFAULT read/edit path.
/// </summary>
public interface IVersionJobRunner
{
    /// <summary>
    /// Starts an asynchronous reconcile job for a version and returns the created job handle. The job is
    /// recorded as <see cref="VersionJobStatus.Pending"/> and executes in the background under the
    /// version lock; poll <see cref="GetJobAsync"/> for completion.
    /// </summary>
    /// <param name="service">Owning service identity (lock scope).</param>
    /// <param name="versionId">Version to reconcile.</param>
    /// <param name="policy">Auto-resolution policy.</param>
    /// <param name="cancellationToken">Cancellation token for the start request (not the job).</param>
    /// <returns>The created job record.</returns>
    Task<VersionJob> StartReconcileAsync(
        string service,
        Guid versionId,
        VersionReconcilePolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an asynchronous post job for a version and returns the created job handle.
    /// </summary>
    /// <param name="service">Owning service identity (lock scope).</param>
    /// <param name="versionId">Version to post.</param>
    /// <param name="cancellationToken">Cancellation token for the start request (not the job).</param>
    /// <returns>The created job record.</returns>
    Task<VersionJob> StartPostAsync(
        string service,
        Guid versionId,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a job record by id for status polling, or null when unknown.</summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The job record, or null.</returns>
    Task<VersionJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
