// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// The immutable, machine-shaped result of <c>honua gp plan</c> (GP Devkit P5,
/// issue #2126): the validated, ordered dry-run plan for a single process plus a
/// best-effort output-size / cost estimate. Produced WITHOUT executing the
/// process (no Redis, no job store, no executor invocation), so it can be shown
/// to the operator before submit and is the same magnitude a future devops-agent
/// step would size serverless Batch resources from.
/// </summary>
internal sealed record GpPlan
{
    /// <summary>Canonical dotted process id the plan targets.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Human-readable process title from the catalog.</summary>
    public required string Title { get; init; }

    /// <summary>Top-level process category (e.g. <c>geometry</c>, <c>raster</c>).</summary>
    public required string Category { get; init; }

    /// <summary>
    /// The runtime profile the job would run under (<c>managed</c> or
    /// <c>native</c>), read from the catalog — the routing/resource hint a
    /// serverless sizing step consumes.
    /// </summary>
    public required string RuntimeProfile { get; init; }

    /// <summary>Whether the plan passed validation and could be submitted as-is.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Validation errors that block submission (param + structural).</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>Non-fatal advisories (e.g. size/cost cautions) for the operator.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>The ordered steps the plan would execute.</summary>
    public required IReadOnlyList<GpPlanStep> Steps { get; init; }

    /// <summary>Declared output artifact kinds for the process.</summary>
    public required IReadOnlyList<ArtifactKind> Outputs { get; init; }

    /// <summary>The output-size / cost estimate (always a heuristic).</summary>
    public required GpSizeEstimate Estimate { get; init; }
}

/// <summary>One resolved step in a <see cref="GpPlan"/>.</summary>
internal sealed record GpPlanStep
{
    /// <summary>Step identifier within the plan DAG.</summary>
    public required string StepId { get; init; }

    /// <summary>The process id this step runs.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Step identifiers that must complete before this one.</summary>
    public required IReadOnlyList<string> DependsOn { get; init; }

    /// <summary>
    /// The resolved parameters for this step, in declaration order, each with a
    /// note on whether the value came from the caller or a catalog default.
    /// </summary>
    public required IReadOnlyList<GpResolvedParam> Parameters { get; init; }
}

/// <summary>A single resolved parameter shown in the plan preview.</summary>
internal sealed record GpResolvedParam
{
    /// <summary>Parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Declared value type from the schema.</summary>
    public required ProcessParameterValueType ValueType { get; init; }

    /// <summary>Whether the parameter is declared-required.</summary>
    public required bool Required { get; init; }

    /// <summary>
    /// The resolved, display-safe value (large/binary values are abbreviated),
    /// or <c>null</c> when the parameter is unset and has no default.
    /// </summary>
    public required string? DisplayValue { get; init; }

    /// <summary>Where the value came from: caller, default, or unset.</summary>
    public required GpParamSource Source { get; init; }
}

/// <summary>Provenance of a resolved parameter value.</summary>
internal enum GpParamSource
{
    /// <summary>Supplied by the caller (<c>--param</c> or bound <c>--input</c>).</summary>
    Caller,

    /// <summary>Filled from the catalog default value.</summary>
    Default,

    /// <summary>Not supplied and no default available.</summary>
    Unset,
}

/// <summary>
/// A best-effort output-size and cost estimate. Every field is a HEURISTIC,
/// never a guarantee — the actual artifact size depends on geometry complexity,
/// compression, and the operation's real selectivity, none of which are known
/// without running. The estimate exists to warn the operator BEFORE submit that
/// a run is likely to exceed the 50 MiB <c>MaxArtifactBytes</c> cap (the real
/// failure mode — the job FAILS at that cap today) or to be long-running.
/// </summary>
internal sealed record GpSizeEstimate
{
    /// <summary>Total caller-supplied input payload size, in bytes.</summary>
    public required long InputBytes { get; init; }

    /// <summary>
    /// Heuristic estimated output payload size, in bytes. <c>null</c> when no
    /// file-like input was supplied to anchor the estimate.
    /// </summary>
    public required long? EstimatedOutputBytes { get; init; }

    /// <summary>The configured <c>MaxArtifactBytes</c> cap the estimate is judged against.</summary>
    public required long MaxArtifactBytes { get; init; }

    /// <summary>
    /// A short human-readable explanation of how the output estimate was derived
    /// (the multiplier and its basis), so the operator can judge its trust.
    /// </summary>
    public required string Basis { get; init; }

    /// <summary>True when the estimated output is likely to exceed the cap.</summary>
    public bool ExceedsCap =>
        EstimatedOutputBytes is { } bytes && bytes > MaxArtifactBytes;

    /// <summary>
    /// True when the operation is flagged as potentially long-running / cost
    /// sensitive (e.g. a native raster/surface op over many tiles), so the
    /// operator and a serverless sizing step can budget for it.
    /// </summary>
    public required bool LongRunning { get; init; }
}
