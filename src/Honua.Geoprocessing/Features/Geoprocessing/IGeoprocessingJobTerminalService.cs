// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Provides the bounded, protocol-neutral terminal wait, result, and cancellation path for
/// geoprocessing adapters.
/// </summary>
internal interface IGeoprocessingJobTerminalService
{
    /// <summary>Waits for a visible job to reach a terminal state within a fixed budget.</summary>
    Task<GeoprocessingTerminalWaitResult> WaitForTerminalAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);

    /// <summary>Waits for a terminal job and resolves its canonical result package.</summary>
    Task<GeoprocessingTerminalResult> WaitForResultAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);

    /// <summary>Requests cancellation within a fixed budget and returns a typed runtime outcome.</summary>
    Task<GeoprocessingCancelResult> CancelAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);

    /// <summary>
    /// Dispatches best-effort cancellation for a job abandoned by a bounded synchronous
    /// request. Cleanup runs outside the response path.
    /// </summary>
    void DispatchOrphanedCancellation(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout);

    /// <summary>Cancels a job orphaned by an abandoned bounded synchronous request.</summary>
    Task<GeoprocessingCancelResult> CancelOrphanedAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);
}

/// <summary>Terminal wait outcome.</summary>
internal enum GeoprocessingTerminalWaitOutcome
{
    /// <summary>The job reached a terminal state.</summary>
    Terminal,
    /// <summary>The job was not found or was not visible to the caller.</summary>
    NotFound,
    /// <summary>The bounded wait expired.</summary>
    Timeout,
    /// <summary>The client disconnected before the wait completed.</summary>
    ClientDisconnected,
}

/// <summary>Typed result of a bounded terminal wait.</summary>
internal sealed record GeoprocessingTerminalWaitResult(
    GeoprocessingTerminalWaitOutcome Outcome,
    ExecutionJobRecord? Job = null);

/// <summary>Terminal result outcome.</summary>
internal enum GeoprocessingTerminalResultOutcome
{
    /// <summary>The job succeeded and its result package was resolved.</summary>
    Succeeded,
    /// <summary>The job failed.</summary>
    Failed,
    /// <summary>The job was cancelled.</summary>
    Cancelled,
    /// <summary>The job was not found or was not visible to the caller.</summary>
    NotFound,
    /// <summary>The bounded wait expired.</summary>
    Timeout,
    /// <summary>The client disconnected before the wait completed.</summary>
    ClientDisconnected,
}

/// <summary>Typed result of a bounded wait plus result resolution.</summary>
internal sealed record GeoprocessingTerminalResult(
    GeoprocessingTerminalResultOutcome Outcome,
    ExecutionJobRecord? Job = null,
    AnalysisResultPackage? ResultPackage = null);

/// <summary>Cancellation outcome exposed to protocol adapters.</summary>
internal enum GeoprocessingCancelOutcome
{
    /// <summary>Cancellation was accepted or the job is cancelled.</summary>
    Cancelled,
    /// <summary>The job had already reached a terminal state.</summary>
    AlreadyTerminal,
    /// <summary>The selected backend does not support cancellation.</summary>
    Unsupported,
    /// <summary>Cancellation could not be confirmed after bounded retries.</summary>
    Unconfirmed,
    /// <summary>The job was not found or was not visible to the caller.</summary>
    NotFound,
    /// <summary>The bounded cancellation attempt expired.</summary>
    Timeout,
    /// <summary>The client disconnected before cancellation completed.</summary>
    ClientDisconnected,
}

/// <summary>Typed result of a bounded cancellation request.</summary>
internal sealed record GeoprocessingCancelResult(
    GeoprocessingCancelOutcome Outcome,
    ExecutionJobRecord? Job = null);
