// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The single, frozen description of one platform capability — the row in the
/// unified capability registry (ADR-0058, Decision B). Every capability-facing
/// surface derives from this descriptor rather than keeping its own roster:
/// the <c>/mcp</c> catalog, the <c>geospatial-mcp</c> conformance manifest, the
/// <c>honua.capability_manifest.v1</c> wire (#1186), Studio AI, the SDK
/// projections (Tracks C/D), and Console.
/// </summary>
/// <remarks>
/// <para>
/// <b>This shape is frozen before the SDK/Console fan-out.</b> Post-freeze
/// changes must be additive and follow the normal contract-version discipline
/// (ADR-0058 "Freeze discipline"). Add new <c>init</c> properties; do not
/// reorder, rename, or remove existing ones.
/// </para>
/// <para>
/// The record uses <c>init</c> properties (not positional parameters) so that
/// adding a field is a source- and binary-additive change that does not disturb
/// downstream projections.
/// </para>
/// </remarks>
public sealed record CapabilityDescriptor
{
    /// <summary>
    /// Stable, unique capability identifier (for example <c>temporal.filtering</c>,
    /// <c>protocol.mcp.tool.plan_analysis</c>, or <c>format.geoparquet</c>). This is
    /// the primary key downstream surfaces bind to and must never change once
    /// frozen.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Grouping category for the capability. For <c>/mcp</c> tool descriptors this
    /// is the MCP workflow family (<c>planning</c>/<c>execution</c>/<c>lifecycle</c>/
    /// <c>results</c>); for manifest capabilities it is the #1186 category
    /// (<c>temporal</c>, <c>packages</c>, …); for resources it is <c>resource</c>;
    /// for data formats it is <c>format</c>.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>The broad kind of capability.</summary>
    public required CapabilityKind Kind { get; init; }

    /// <summary>The product-lifecycle maturity (shared enum; see <see cref="CapabilityMaturity"/>).</summary>
    public required CapabilityMaturity Maturity { get; init; }

    /// <summary>
    /// The existing <see cref="FeatureCatalog"/> entitlement key that gates this
    /// capability, or <c>null</c> when the capability is ungated. When set, this is
    /// the same string used by license entitlement checks.
    /// </summary>
    public string? EntitlementKey { get; init; }

    /// <summary>
    /// The minimum edition required, derived from <see cref="FeatureCatalog"/> for
    /// the <see cref="EntitlementKey"/>, or <c>null</c> when the capability is
    /// ungated or has no catalog mapping.
    /// </summary>
    public HonuaEdition? MinimumEdition { get; init; }

    /// <summary>
    /// The bare <c>geospatial-mcp</c> taxonomy name this capability implements
    /// (a tool's <c>standardName</c>, or a resource family's standard label), or
    /// <c>null</c> when the capability has no standard-vocabulary name. Mirrors
    /// the <c>standardName</c>/<c>family</c> fields the <c>CapabilityManifestEmitter</c>
    /// (#2338) emits so the registry and the emitter cannot diverge.
    /// </summary>
    public string? StandardName { get; init; }

    /// <summary>
    /// The name the live <c>/mcp</c> surface advertises for this capability on
    /// <c>tools/list</c> (for example <c>honua_plan_analysis</c>), or <c>null</c> for
    /// non-tool capabilities. Mirrors the emitter's <c>advertisedName</c>.
    /// </summary>
    public string? McpToolName { get; init; }

    /// <summary>
    /// The <c>honua://</c> resource-URI templates this capability serves
    /// (Decision A grammar), or empty for non-resource capabilities. Mirrors the
    /// emitter's <c>uriForm</c> and the <c>McpResourceUris</c> seam.
    /// </summary>
    public IReadOnlyList<string> ResourceUriTemplates { get; init; } = [];

    /// <summary>
    /// The package/manifest schema version this capability is expressed under
    /// (for example a package-family schema version), or <c>null</c>. Populated by
    /// B3 (#2335) when the #1186 package families layer onto the registry.
    /// </summary>
    public string? PackageSchemaVersion { get; init; }

    /// <summary>
    /// Whether the live surface actually serves this capability today, or declares
    /// it as an honest gap. See <see cref="CapabilityImplementationStatus"/>.
    /// </summary>
    public CapabilityImplementationStatus ImplementationStatus { get; init; }
        = CapabilityImplementationStatus.Served;

    /// <summary>
    /// The <c>geospatial-mcp</c> conformance-manifest section this capability is
    /// asserted under (<c>tools</c> or <c>resources</c>), or <c>null</c> when the
    /// capability does not participate in <c>geospatial-mcp</c> conformance
    /// (manifest capabilities, data formats). Lets a downstream surface tell which
    /// capabilities the conformance harness pins.
    /// </summary>
    public string? ConformanceMapping { get; init; }
}
