// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Canonical parameter keys stored on execution-job specs when submit paths need
/// to preserve workflow metadata for reconciliation and progress projection.
/// </summary>
internal static class ExecutionJobParameterKeys
{
    /// <summary>
    /// Stable analysis plan identifier for geoprocessing execution jobs.
    /// </summary>
    public const string GeoprocessingPlanId = "honua.geoprocessing.plan_id";
}
