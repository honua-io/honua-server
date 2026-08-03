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
/// values. A violation counts as presence-based when withdrawing some <em>other</em> supplied
/// parameter clears it (a genuine mutually-exclusive-input conflict) or when supplying one
/// more parameter clears it (an unsatisfied branch requirement, such as an
/// exactly-one-of group that nothing in the supplied set satisfies). A complaint about a
/// probe-substituted value survives both and is never reported. This is the seam consumed by
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
    /// This is the CONSERVATIVE half of the answer and applies only where the branch space is
    /// genuinely unenumerable: a supplied parameter whose legal values the catalog does not
    /// declare (<c>transform.computed-field</c>'s <c>op</c>) lets a caller select a branch the
    /// probe never visits, so every candidate the discriminator could turn on is returned.
    /// Where every supplied discriminator declares <c>AllowedValues</c>, the branch space IS
    /// enumerable and this method returns nothing —
    /// <see cref="FindConditionalBranchRequirements"/> answers exactly instead (#3048).
    /// </para>
    /// </remarks>
    /// <param name="processId">Canonical process identifier.</param>
    /// <param name="suppliedParameterNames">Parameter names the caller would supply.</param>
    /// <returns>
    /// Unsupplied parameter names whose conditional requiredness cannot be ruled out, or an
    /// empty list when every omission is unconditionally optional or provable branch-by-branch.
    /// </returns>
    IReadOnlyList<string> FindUnverifiableConditionalParameters(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);

    /// <summary>
    /// Returns the EXACT branch-qualified missing-input requirements for
    /// <paramref name="processId"/>, by enumerating a bounded cross-product over the declared
    /// <see cref="Domain.ProcessParameterSpec.AllowedValues"/> of the supplied parameters and
    /// reporting the union of the canonical validator's missing-input violations, each tagged
    /// with the assignment that raised it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the PRECISE half of the answer, available since the catalog publishes the
    /// finite value domains the plan validator enforces (#3048). A mapping that omits
    /// <c>analytics.cluster-managed</c>'s <c>k</c> is no longer merely "unverifiable": the
    /// probe can state that <c>algorithm=kmeans</c> requires it and that no other branch does.
    /// A mapping no branch faults is provably executable for every admissible value and
    /// carries no entry at all.
    /// </para>
    /// <para>
    /// Only requirements that some branch raises and another branch clears are reported.
    /// A requirement common to EVERY branch is unconditional, not branch-dependent, and is
    /// already reported by <see cref="FindAdmissibilityViolations"/>. The result is empty when
    /// the branch space is not enumerable; that case stays with
    /// <see cref="FindUnverifiableConditionalParameters"/>.
    /// </para>
    /// </remarks>
    /// <param name="processId">Canonical process identifier.</param>
    /// <param name="suppliedParameterNames">Parameter names the caller would supply.</param>
    /// <returns>
    /// Branch-qualified requirements, or an empty list when no enumerable branch requires an
    /// unsupplied parameter.
    /// </returns>
    IReadOnlyList<ProcessBranchRequirement> FindConditionalBranchRequirements(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);
}

/// <summary>
/// A missing-input requirement the canonical plan validator raises on one specific
/// discriminator branch and not on others.
/// </summary>
/// <param name="Branch">
/// The discriminator assignment that raises it, formatted as comma-separated
/// <c>name=value</c> pairs (for example <c>algorithm=kmeans</c>).
/// </param>
/// <param name="ParameterName">Canonical parameter the branch requires.</param>
/// <param name="Message">Human-readable explanation taken from the canonical validator.</param>
public readonly record struct ProcessBranchRequirement(
    string Branch,
    string ParameterName,
    string Message);

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
    /// The process cannot complete as a job at all, regardless of parameters — either it runs
    /// only through a synchronous protocol surface, so the job runtime rejects it, or it is
    /// advertised for discoverability while its executor fails every job in this build.
    /// </summary>
    NotJobExecutable
}
