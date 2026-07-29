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
    /// Human-readable violation messages, or an empty list when the supplied set satisfies
    /// every presence-based requirement.
    /// </returns>
    IReadOnlyList<string> FindAdmissibilityViolations(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);
}
