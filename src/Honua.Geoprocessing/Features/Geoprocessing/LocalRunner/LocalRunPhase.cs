// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.LocalRunner;

/// <summary>
/// One entry in the GP Devkit glass-box phase timeline (issue #2128): a phase label the
/// executor reported via <c>ReportProgressAsync</c>, the optional percent-complete it
/// stamped, and the wall-clock elapsed time from the start of the run when the phase
/// began. For a workflow DAG each step's phases appear in order, giving per-step timing.
/// </summary>
public sealed record LocalRunPhase
{
    /// <summary>The phase label the executor reported (e.g. <c>Running ogr2ogr conversion</c>).</summary>
    public required string Phase { get; init; }

    /// <summary>The percent-complete the executor stamped, or <c>null</c> if it reported none.</summary>
    public double? PercentComplete { get; init; }

    /// <summary>Wall-clock elapsed from the start of the run when this phase was reported.</summary>
    public TimeSpan Elapsed { get; init; }
}
