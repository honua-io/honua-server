// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>Host-owned admission for explicitly supported bounded managed agent processes.</summary>
/// <remarks>Missing adapters and unsupported process/source contracts refuse; this is not generic execution authority.</remarks>
public interface IAgentManagedProcessAdmission
{
    /// <summary>Checks the actual authenticated request, exact process grant, original live turn and source ceilings.</summary>
    /// <param name="plan">Actual submitted plan.</param>
    /// <param name="executor">Actual request principal, never a reconstructed policy identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Original credential-aware row/field policy capture, without execution authority.</returns>
    Task<JobSecurityContext> CaptureAsync(AnalysisPlan plan, ClaimsPrincipal executor, CancellationToken cancellationToken = default);

    /// <summary>Seals the final server-prepared immutable job immediately before first durable acceptance.</summary>
    /// <param name="job">Final job after canonical plan/workload validation.</param>
    /// <param name="plan">Actual validated plan.</param>
    /// <param name="executor">Actual current request principal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The same intent with server-owned provenance and bounded execution lease.</returns>
    Task<ExecutionJobRecord> SealAsync(ExecutionJobRecord job, AnalysisPlan plan, ClaimsPrincipal executor, CancellationToken cancellationToken = default);

    /// <summary>Checks the original accepted turn and exact current authority before acknowledging or repairing a replay.</summary>
    /// <param name="job">Actual original accepted job.</param>
    /// <param name="executor">Actual current request principal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireReplayAsync(ExecutionJobRecord job, ClaimsPrincipal executor, CancellationToken cancellationToken = default);
}
