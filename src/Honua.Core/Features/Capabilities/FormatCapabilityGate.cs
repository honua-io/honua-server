// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The shared format-negotiation gate (#2342 / T6). Every format-negotiation seam
/// — the GeoServices query output formatter, the OGC/OData converters, and the
/// file-import reader dispatch — consults this gate <b>before</b> dispatching to a
/// reader or writer, so a data format the unified capability registry (ADR-0058)
/// reports as gated off (an <see cref="CapabilityMaturity.Experimental"/> format
/// whose flag is unset, or a format the active edition is not entitled to) is
/// rejected with a clear reason instead of being silently served or silently
/// coerced to another format.
/// </summary>
/// <remarks>
/// <para>
/// The gate is a thin projection over <see cref="ICapabilityRegistry.Resolve"/>
/// (the T2 <see cref="CapabilityGateResolver"/> precedence): it maps a data-format
/// name (the segment after <see cref="CapabilityRegistry.DataFormatIdPrefix"/>, for
/// example <c>geoparquet</c>) to its <c>format.*</c> descriptor, resolves it against
/// the supplied <see cref="CapabilityGateContext"/>, and translates the registry
/// reason code into a format-scoped one (<see cref="FormatCapabilityReasonCodes"/>).
/// </para>
/// <para>
/// In T6 every registered format is still <see cref="CapabilityMaturity.Implemented"/>,
/// so the gate reports <see cref="FormatGateStatus.Enabled"/> for every registered
/// format — no format is disabled in this ticket. The experimental flips land in T10
/// (#2346) and flow through this seam unchanged: a flipped-and-flag-off format then
/// resolves <see cref="FormatGateStatus.ExperimentalDisabled"/> here.
/// </para>
/// <para>
/// A format the registry does not manage resolves <see cref="FormatGateStatus.Unknown"/>
/// (it is not a <c>format.*</c> descriptor). The gate does not itself reject an
/// unknown format — the seam owns whether an unrecognised token is a hard error
/// (see <see cref="FormatGateDecision.IsBlocked"/>): a seam that already validates
/// its wire tokens against a fixed supported set keeps that behaviour, while the
/// gate only adds the <see cref="FormatGateStatus.ExperimentalDisabled"/> /
/// <see cref="FormatGateStatus.LicenseRequired"/> rejection on top.
/// </para>
/// </remarks>
public static class FormatCapabilityGate
{
    // A default registry backed by the same static roster CapabilityRegistry.All
    // exposes, so seams that have no registry in scope (the deep formatter/import
    // dispatch paths) can still consult the gate without new DI plumbing. Tests and
    // future DI callers can pass an explicit registry.
    private static readonly CapabilityRegistry SharedRegistry = new();

    /// <summary>
    /// Evaluates a single data-format name (the segment after
    /// <see cref="CapabilityRegistry.DataFormatIdPrefix"/>, for example
    /// <c>geoparquet</c> or <c>esrijson</c>) against the capability registry.
    /// </summary>
    /// <param name="formatName">
    /// The registry data-format name (case-insensitive), <b>not</b> a protocol wire
    /// token — the seam is responsible for mapping its wire token (for example the
    /// GeoServices <c>f=json</c>) to the registry name (<c>esrijson</c>) first.
    /// </param>
    /// <param name="context">
    /// The edition/environment/experimental-flag inputs, or <c>null</c> for
    /// <see cref="CapabilityGateContext.Default"/> (Community edition, flags off).
    /// </param>
    /// <param name="registry">
    /// The capability registry to resolve against, or <c>null</c> for the shared
    /// default registry.
    /// </param>
    public static FormatGateDecision Evaluate(
        string formatName,
        CapabilityGateContext? context = null,
        ICapabilityRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);

        var effectiveRegistry = registry ?? SharedRegistry;
        var effectiveContext = context ?? CapabilityGateContext.Default;
        var capabilityId = CapabilityRegistry.DataFormatIdPrefix + formatName.Trim().ToLowerInvariant();

        return FormatGateDecision.FromResolution(effectiveRegistry.Resolve(capabilityId, effectiveContext));
    }

    /// <summary>
    /// Filters an advertised list of data-format names down to those that are not
    /// gated off, preserving order and dropping only registry-managed formats the
    /// gate <see cref="FormatGateDecision.IsBlocked"/> reports as blocked. Names the
    /// registry does not manage (<see cref="FormatGateStatus.Unknown"/>) are kept —
    /// the advertising seam owns those.
    /// </summary>
    /// <param name="formatNames">The candidate registry data-format names.</param>
    /// <param name="context">The gate context, or <c>null</c> for the default.</param>
    /// <param name="registry">The registry, or <c>null</c> for the shared default.</param>
    public static IReadOnlyList<string> FilterAdvertised(
        IEnumerable<string> formatNames,
        CapabilityGateContext? context = null,
        ICapabilityRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(formatNames);

        var kept = new List<string>();
        foreach (var name in formatNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!Evaluate(name, context, registry).IsBlocked)
            {
                kept.Add(name);
            }
        }

        return kept;
    }
}

/// <summary>
/// The outcome of a <see cref="FormatCapabilityGate"/> evaluation.
/// </summary>
public enum FormatGateStatus
{
    /// <summary>The format is registered and enabled for the context.</summary>
    Enabled = 0,

    /// <summary>
    /// The format is not managed by the capability registry (no <c>format.*</c>
    /// descriptor). Not a rejection on its own — the seam decides.
    /// </summary>
    Unknown = 1,

    /// <summary>
    /// The format is an <see cref="CapabilityMaturity.Experimental"/> format whose
    /// per-capability/global flag is unset, so it is off by default and must be
    /// rejected.
    /// </summary>
    ExperimentalDisabled = 2,

    /// <summary>
    /// The format's <see cref="CapabilityDescriptor.MinimumEdition"/> exceeds the
    /// active edition, so it must be rejected on entitlement.
    /// </summary>
    LicenseRequired = 3,
}

/// <summary>
/// A <see cref="FormatCapabilityGate"/> decision: the <see cref="Status"/> plus a
/// stable, format-scoped <see cref="ReasonCode"/> when the format is not enabled.
/// </summary>
/// <param name="Status">The gate status.</param>
/// <param name="ReasonCode">
/// The <see cref="FormatCapabilityReasonCodes"/> value when <see cref="Status"/> is
/// not <see cref="FormatGateStatus.Enabled"/>, or <c>null</c> when enabled.
/// </param>
public readonly record struct FormatGateDecision(FormatGateStatus Status, string? ReasonCode)
{
    /// <summary>Whether the format is registered and enabled for the context.</summary>
    public bool IsEnabled => Status == FormatGateStatus.Enabled;

    /// <summary>
    /// Whether the format is registry-managed <b>and</b> gated off, so the seam must
    /// reject it (return a 400). An <see cref="FormatGateStatus.Unknown"/> format is
    /// <b>not</b> blocked — it is simply not registry-managed.
    /// </summary>
    public bool IsBlocked => Status is FormatGateStatus.ExperimentalDisabled or FormatGateStatus.LicenseRequired;

    internal static FormatGateDecision FromResolution(CapabilityResolution resolution)
        => resolution.Enabled
            ? new FormatGateDecision(FormatGateStatus.Enabled, null)
            : resolution.ReasonCode switch
            {
                CapabilityReasonCodes.ExperimentalDisabled =>
                    new FormatGateDecision(FormatGateStatus.ExperimentalDisabled, FormatCapabilityReasonCodes.ExperimentalDisabled),
                CapabilityReasonCodes.LicenseRequired =>
                    new FormatGateDecision(FormatGateStatus.LicenseRequired, FormatCapabilityReasonCodes.LicenseRequired),
                _ => new FormatGateDecision(FormatGateStatus.Unknown, FormatCapabilityReasonCodes.Unknown),
            };
}

/// <summary>
/// Stable, machine-readable reason codes a format-negotiation seam returns on a 400
/// when <see cref="FormatCapabilityGate"/> rejects a format. Format-scoped mirrors of
/// the registry-layer <see cref="CapabilityReasonCodes"/> so the wire contract names
/// the <c>format</c> axis explicitly.
/// </summary>
public static class FormatCapabilityReasonCodes
{
    /// <summary>The requested format is not a recognised/registered data format.</summary>
    public const string Unknown = "format-unknown";

    /// <summary>
    /// The requested format is experimental and off by default (its experimental flag
    /// is unset). Returned on the <c>f</c>/<c>outputFormat</c> value.
    /// </summary>
    public const string ExperimentalDisabled = "format-experimental-disabled";

    /// <summary>The active edition is not entitled to the requested format.</summary>
    public const string LicenseRequired = "format-license-required";
}
