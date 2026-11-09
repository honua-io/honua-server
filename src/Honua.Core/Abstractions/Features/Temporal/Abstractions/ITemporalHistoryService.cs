// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Abstractions;

public interface ITemporalHistoryService
{
    Task<TemporalCapability> GetCapabilityAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<TemporalAsOfResult> ReadAsOfAsync(
        string serviceId,
        int layerId,
        TemporalAsOfRequest request,
        CancellationToken cancellationToken = default);

    Task<TemporalDiffResult> DiffAsync(
        string serviceId,
        int layerId,
        TemporalDiffRequest request,
        CancellationToken cancellationToken = default);

    Task<TemporalTimeline> GetTimelineAsync(
        string serviceId,
        int layerId,
        TemporalTimelineRequest request,
        CancellationToken cancellationToken = default);

    Task<TemporalRollbackPlan> PlanRollbackAsync(
        string serviceId,
        int layerId,
        TemporalRollbackPlanRequest request,
        CancellationToken cancellationToken = default);

    Task<TemporalRollbackJobHandle> ExecuteRollbackAsync(
        string serviceId,
        int layerId,
        TemporalRollbackExecuteRequest request,
        CancellationToken cancellationToken = default);
}
