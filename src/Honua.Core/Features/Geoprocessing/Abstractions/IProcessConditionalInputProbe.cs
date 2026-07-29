// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Exposes the canonical plan validator's <em>presence-based</em> input requirements to
/// callers that need to know whether a given set of supplied parameter names would be
/// admissible, without executing or duplicating the validator's semantics.
/// </summary>
/// <remarks>
/// <para>
/// Static <c>Required</c> flags on <see cref="Domain.ProcessParameterSpec"/> are not the
/// whole admissibility contract: several processes declare mutually-substitutable optional
/// inputs (for example the raster <c>source</c>/<c>layerId</c>/<c>rasterId</c> trio) that
/// only the canonical plan validator enforces at submit time. Migration/translation
/// tooling must not certify a tool the submit path will reject, and must not re-implement
/// the conditional rules — that would create a second, drift-prone enforcement point.
/// </para>
/// <para>
/// Implementations answer strictly from parameter <em>presence</em>; they must not report
/// value-format violations, because callers supply parameter names rather than real
/// values. A violation counts as presence-based when it disappears once some <em>other</em>
/// supplied parameter is withdrawn — that separates a genuine mutually-exclusive-input
/// conflict from a complaint about a probe-substituted value. This is the seam consumed by
/// the arcpy/toolbox translation lane (#2145).
/// </para>
/// </remarks>
public interface IProcessConditionalInputProbe
{
    /// <summary>
    /// Returns the canonical presence-based violations a plan step for
    /// <paramref name="processId"/> would raise when exactly
    /// <paramref name="suppliedParameterNames"/> are supplied. This covers both missing
    /// required inputs (including conditionally-required ones) and conflicts between
    /// mutually-exclusive inputs.
    /// </summary>
    /// <param name="processId">Canonical process identifier.</param>
    /// <param name="suppliedParameterNames">Parameter names the caller would supply.</param>
    /// <returns>
    /// The violations the submit path would raise, or an empty list when the supplied set is
    /// admissible.
    /// </returns>
    IReadOnlyList<ProcessAdmissibilityViolation> FindAdmissibilityViolations(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);

    /// <summary>
    /// Returns the parameters that are neither supplied nor defaulted and whose requiredness
    /// depends on a caller-supplied value the probe cannot pin down, so no static report can
    /// prove the process executes for every admissible input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately narrower than "every unsupplied, defaultless parameter". The
    /// catalog models no conditional requirements at all — <see cref="Domain.ProcessParameterSpec"/>
    /// carries only <c>Required</c>, <c>DefaultValue</c> and <c>AllowedValues</c> — so they
    /// exist solely as per-process rules inside the canonical plan validator. A parameter that
    /// rule set never requires is unconditionally optional, and omitting it is accepted at
    /// submit time; reporting it would misclassify an executable mapping.
    /// </para>
    /// <para>
    /// The result is conservative in the other direction: when a supplied parameter's value
    /// domain cannot be enumerated (the catalog declares no <c>AllowedValues</c> for a
    /// discriminator such as <c>analytics.cluster-managed</c>'s <c>algorithm</c>), no branch
    /// can be ruled out and every candidate is returned. Claiming a tool executable when a
    /// branch is genuinely unproven is the worse error, so an unenumerable domain always
    /// yields the pessimistic answer.
    /// </para>
    /// </remarks>
    /// <param name="processId">Canonical process identifier.</param>
    /// <param name="suppliedParameterNames">Parameter names the caller would supply.</param>
    /// <returns>
    /// Unsupplied parameter names whose conditional requiredness cannot be ruled out, or an
    /// empty list when every omission is unconditionally optional.
    /// </returns>
    IReadOnlyList<string> FindUnverifiableConditionalParameters(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);
}

/// <summary>
/// A single reason the canonical submit path would reject a proposed parameter set.
/// </summary>
/// <param name="Kind">Classifies why the submission is inadmissible.</param>
/// <param name="Message">Human-readable explanation taken from the canonical validator.</param>
public readonly record struct ProcessAdmissibilityViolation(
    ProcessAdmissibilityViolationKind Kind,
    string Message);

/// <summary>
/// Categories of submit-path rejection surfaced by <see cref="IProcessConditionalInputProbe"/>.
/// </summary>
public enum ProcessAdmissibilityViolationKind
{
    /// <summary>
    /// The supplied parameter set does not satisfy the process's input requirements
    /// (including conditional and mutually-exclusive rules).
    /// </summary>
    Inputs,

    /// <summary>
    /// The process cannot be dispatched as a job at all, regardless of parameters — it runs
    /// only through a synchronous protocol surface, so the job runtime rejects it.
    /// </summary>
    NotJobExecutable
}
