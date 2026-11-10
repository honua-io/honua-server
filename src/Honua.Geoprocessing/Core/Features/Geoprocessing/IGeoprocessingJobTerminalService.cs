// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal interface IGeoprocessingJobTerminalService
{
    Task<GeoprocessingCancelResult> CancelGPServerAsync(
        string jobId, string serviceId, string taskName, ClaimsPrincipal principal,
        TimeSpan timeout, CancellationToken clientDisconnect = default)
        => CancelAsync(jobId, principal, timeout, clientDisconnect);

    void DispatchGPServerOrphanedCancellation(
        string jobId, string serviceId, string taskName, ClaimsPrincipal principal, TimeSpan timeout)
        => DispatchOrphanedCancellation(jobId, principal, timeout);

    Task<GeoprocessingTerminalWaitResult> WaitForTerminalAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);

    Task<GeoprocessingTerminalResult> WaitForResultAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);

    Task<GeoprocessingCancelResult> CancelAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);

    void DispatchOrphanedCancellation(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout);

    Task<GeoprocessingCancelResult> CancelOrphanedAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default);
}

internal enum GeoprocessingTerminalWaitOutcome
{
    Terminal,
    NotFound,
    Timeout,
    ClientDisconnected,
}

internal sealed record GeoprocessingTerminalWaitResult(
    GeoprocessingTerminalWaitOutcome Outcome,
    ExecutionJobRecord? Job = null);

internal enum GeoprocessingTerminalResultOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    NotFound,
    Timeout,
    ClientDisconnected,
}

internal sealed record GeoprocessingTerminalResult(
    GeoprocessingTerminalResultOutcome Outcome,
    ExecutionJobRecord? Job = null,
    AnalysisResultPackage? ResultPackage = null);

internal enum GeoprocessingCancelOutcome
{
    Cancelled,
    AlreadyTerminal,
    Unsupported,
    Unconfirmed,
    NotFound,
    Timeout,
    ClientDisconnected,
}

internal sealed record GeoprocessingCancelResult(
    GeoprocessingCancelOutcome Outcome,
    ExecutionJobRecord? Job = null);
