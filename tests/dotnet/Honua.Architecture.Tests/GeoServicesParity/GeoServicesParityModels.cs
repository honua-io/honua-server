// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Architecture.Tests.GeoServicesParity;

/// <summary>
/// The outcome of joining the derived GeoServices route roster with the hand-authored
/// judgement source. Every mismatch direction is reported as data rather than thrown,
/// so the gate can fail once with the whole picture.
/// </summary>
internal sealed class GeoServicesParityJoin
{
    /// <summary>The derived roster the join was computed against.</summary>
    public required GeoServicesRoster Roster { get; init; }

    /// <summary>The hand-authored judgement source the join was computed against.</summary>
    public required ParityJudgment Judgment { get; init; }

    /// <summary>Served operations no judgement entry claims — we shipped it and never classified it.</summary>
    public required string[] ServedButUnclassified { get; init; }

    /// <summary>Judgement entries naming an operation that is not served — the over-claim direction.</summary>
    public required string[] ClassifiedButNotServed { get; init; }

    /// <summary>Operations claimed by more than one judgement entry, which can disagree on status.</summary>
    public required string[] ClassifiedTwice { get; init; }

    /// <summary>Operations recorded as not-implemented that are in fact served — the under-claim direction.</summary>
    public required string[] NotImplementedButServed { get; init; }

    /// <summary>Operations classified under a matrix service that does not own the serving Esri service type.</summary>
    public required string[] MisfiledUnderWrongService { get; init; }

    /// <summary>Served Esri service types the matrix has no service for.</summary>
    public required string[] ServiceTypesWithNoMatrixHome { get; init; }

    /// <summary>Declared roster exclusions that no longer match any served route.</summary>
    public required string[] StaleExclusions { get; init; }
}

/// <summary>
/// Root document of the hand-authored judgement source.
/// </summary>
/// <remarks>
/// Every collection here is nullable and read through a non-null accessor. That is
/// deliberate rather than defensive noise: this type is deserialized from a file a
/// human edits, so any property can simply be absent, and the source-generated
/// deserializer leaves an omitted property null rather than running its initializer.
/// </remarks>
internal sealed class ParityJudgment
{
    /// <summary>Judgement-source schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Date the judgement layer was last reviewed by a human.</summary>
    public string LastReviewed { get; init; } = string.Empty;

    /// <summary>Issue tracking the derivation (#2863).</summary>
    public string TrackingIssue { get; init; } = string.Empty;

    /// <summary>In-file guidance for the next author.</summary>
    public string Readme { get; init; } = string.Empty;

    /// <summary>Allowed status tokens for operations and parameters, as authored.</summary>
    public Dictionary<string, string[]>? StatusVocabulary { get; init; }

    /// <summary>Per-service judgement, as authored.</summary>
    public JudgmentService[]? Services { get; init; }

    /// <summary>Allowed status tokens; empty when the source omits them.</summary>
    [JsonIgnore]
    public Dictionary<string, string[]> Vocabulary => StatusVocabulary ?? [];

    /// <summary>Per-service judgement; empty when the source omits it.</summary>
    [JsonIgnore]
    public JudgmentService[] ServiceList => Services ?? [];
}

/// <summary>Hand-authored judgement for one GeoServices service family.</summary>
internal sealed class JudgmentService
{
    /// <summary>Stable service id (<c>feature-server</c>, …).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-facing service name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Headline parity verdict for the service.</summary>
    public string Parity { get; init; } = string.Empty;

    /// <summary>Doc a reader should follow for detail.</summary>
    public string DrillDownDoc { get; init; } = string.Empty;

    /// <summary>Prose summary of what the service supports, as authored.</summary>
    public string[]? ImplementedSurface { get; init; }

    /// <summary>Prose summary of the service's headline gaps, as authored.</summary>
    public string[]? KnownGaps { get; init; }

    /// <summary>Code/test paths a reader can use to verify the claims, as authored.</summary>
    public Dictionary<string, string[]>? Evidence { get; init; }

    /// <summary>Classified served operations, keyed by derived <c>esriPath</c>, as authored.</summary>
    public JudgmentOperation[]? Operations { get; init; }

    /// <summary>Esri operations Honua does not serve at all, as authored.</summary>
    public AbsentOperation[]? AbsentOperations { get; init; }

    /// <summary>Parameter-level coverage, grouped by operation family.</summary>
    public Dictionary<string, ParameterCoverageEntry[]>? ParameterCoverage { get; init; }

    /// <summary>Service-level caveats that are not per-operation.</summary>
    public string[]? KnownLimitations { get; init; }

    /// <summary>Classified served operations; empty when the source omits them.</summary>
    [JsonIgnore]
    public JudgmentOperation[] OperationList => Operations ?? [];

    /// <summary>Absent Esri operations; empty when the source omits them.</summary>
    [JsonIgnore]
    public AbsentOperation[] AbsentList => AbsentOperations ?? [];

    /// <summary>Evidence paths; empty when the source omits them.</summary>
    [JsonIgnore]
    public Dictionary<string, string[]> EvidenceMap => Evidence ?? [];
}

/// <summary>One hand-authored judgement, bound to one or more derived operations.</summary>
internal sealed class JudgmentOperation
{
    /// <summary>Human-facing operation (or operation-group) name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Judgement: <c>implemented</c>, <c>partial</c>, or <c>stub</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// The derived Esri-relative operation paths this judgement covers — the join keys —
    /// as authored. Each must be served, and no other entry may claim the same path.
    /// </summary>
    public string[]? EsriPaths { get; init; }

    /// <summary>Set when the operation has no Esri equivalent (a Honua extension).</summary>
    public HonuaExtension? HonuaExtension { get; init; }

    /// <summary>Gap prose: what is and is not supported, and why.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// The join keys; empty when the source omits them, which the gate rejects rather
    /// than letting a judgement float free of any operation.
    /// </summary>
    [JsonIgnore]
    public string[] PathList => EsriPaths ?? [];
}

/// <summary>An Esri operation Honua does not serve.</summary>
internal sealed class AbsentOperation
{
    /// <summary>Human-facing operation name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Esri-relative path of the operation Honua does not serve.</summary>
    public string EsriPath { get; init; } = string.Empty;

    /// <summary>Why it is not served.</summary>
    public string Notes { get; init; } = string.Empty;
}

/// <summary>Honua-extension metadata for an operation with no Esri equivalent.</summary>
internal sealed class HonuaExtension
{
    /// <summary>Extension family id.</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>Edition the extension is gated to.</summary>
    public string Edition { get; init; } = string.Empty;

    /// <summary>Licensing feature key.</summary>
    public string FeatureKey { get; init; } = string.Empty;
}

/// <summary>Parameter-level coverage for one parameter or parameter group.</summary>
internal sealed class ParameterCoverageEntry
{
    /// <summary>Parameter name (or group).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Parameter status token.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Optional detail.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }
}

/// <summary>Root document of the generated, published parity matrix.</summary>
internal sealed class ParityMatrix
{
    /// <summary>Published schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Relative path of the generator that produced this artifact.</summary>
    public string Generator { get; init; } = string.Empty;

    /// <summary>Issue that made the roster derived (#2863).</summary>
    public string TrackingIssue { get; init; } = string.Empty;

    /// <summary>Date the judgement layer was last reviewed by a human.</summary>
    public string LastReviewed { get; init; } = string.Empty;

    /// <summary>Relative path of the hand-authored judgement source.</summary>
    public string JudgmentSource { get; init; } = string.Empty;

    /// <summary>Where the route roster comes from and how it is normalized.</summary>
    public ParityRosterProvenance RouteRoster { get; init; } = new();

    /// <summary>Canonical published locations for this matrix.</summary>
    public ParityCanonicalDocs CanonicalDocs { get; init; } = new();

    /// <summary>Allowed status tokens.</summary>
    public Dictionary<string, string[]> StatusVocabulary { get; init; } = [];

    /// <summary>Per-service parity.</summary>
    public ParityService[] Services { get; init; } = [];
}

/// <summary>Provenance of the derived half of the matrix.</summary>
internal sealed class ParityRosterProvenance
{
    /// <summary>The generated capability data the roster is projected from.</summary>
    public string DerivedFrom { get; init; } = string.Empty;

    /// <summary>The served-route → <c>esriPath</c> normalization, stated in full.</summary>
    public string Normalization { get; init; } = string.Empty;

    /// <summary>Which fields are derived and must not be hand-edited.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Esri service types deliberately outside this matrix, with reasons.</summary>
    public ParityExclusion[] ExcludedServiceTypes { get; init; } = [];
}

/// <summary>A service type deliberately excluded from the matrix.</summary>
internal sealed class ParityExclusion
{
    /// <summary>Excluded Esri service type.</summary>
    public string ServiceType { get; init; } = string.Empty;

    /// <summary>Why it is out of scope.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Canonical published locations.</summary>
internal sealed class ParityCanonicalDocs
{
    /// <summary>Human-facing summary page.</summary>
    public string LandingPage { get; init; } = string.Empty;

    /// <summary>This generated artifact.</summary>
    public string MachineReadableExport { get; init; } = string.Empty;

    /// <summary>The hand-authored judgement source behind it.</summary>
    public string JudgmentSource { get; init; } = string.Empty;
}

/// <summary>Published parity for one GeoServices service family.</summary>
internal sealed class ParityService
{
    /// <summary>Stable service id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-facing service name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Headline parity verdict.</summary>
    public string Parity { get; init; } = string.Empty;

    /// <summary>Doc a reader should follow for detail.</summary>
    public string DrillDownDoc { get; init; } = string.Empty;

    /// <summary>Prose summary of what the service supports.</summary>
    public string[] ImplementedSurface { get; init; } = [];

    /// <summary>Prose summary of the service's headline gaps.</summary>
    public string[] KnownGaps { get; init; } = [];

    /// <summary>Code/test paths a reader can use to verify the claims.</summary>
    public Dictionary<string, string[]> Evidence { get; init; } = [];

    /// <summary>Operations bucketed by status.</summary>
    public ParityOperationBuckets Operations { get; init; } = new();

    /// <summary>Served operations with no Esri equivalent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParityOperation[]? HonuaExtensions { get; init; }

    /// <summary>Parameter-level coverage.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ParameterCoverageEntry[]>? ParameterCoverage { get; init; }

    /// <summary>Service-level caveats.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? KnownLimitations { get; init; }
}

/// <summary>Operations grouped by their hand-authored status.</summary>
internal sealed class ParityOperationBuckets
{
    /// <summary>Operations whose documented Esri behaviour is supported.</summary>
    public ParityOperation[] Implemented { get; init; } = [];

    /// <summary>Operations supporting only a subset of documented behaviour.</summary>
    public ParityOperation[] Partial { get; init; } = [];

    /// <summary>
    /// Operations whose route exists and returns the spec-shaped response while the
    /// backing model is deferred: reads return empty/<c>false</c>, mutations return 400.
    /// </summary>
    public ParityOperation[] Stub { get; init; } = [];

    /// <summary>Esri operations Honua does not serve.</summary>
    public AbsentOperation[] NotImplemented { get; init; } = [];
}

/// <summary>One published operation: derived route facts plus the hand-authored judgement.</summary>
internal sealed class ParityOperation
{
    /// <summary>Human-facing operation (or operation-group) name. Authored.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Judgement status. Authored.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Esri-relative operation paths. DERIVED — do not hand-edit.</summary>
    public string[] EsriPaths { get; init; } = [];

    /// <summary>Every served <c>METHOD /path</c> behind those operations. DERIVED — do not hand-edit.</summary>
    public string[] HonuaEndpoints { get; init; } = [];

    /// <summary>
    /// Capability-registry maturity tiers (ADR-0058) of the served routes behind this
    /// operation. DERIVED — do not hand-edit. An <c>experimental</c> tier means the route
    /// is gated off the first-release surface and 404s unless its capability is explicitly
    /// enabled.
    /// <para>
    /// This is <b>not</b> <see cref="Status"/> and must not be read as one: it answers
    /// "is this route in the release?", where <see cref="Status"/> answers "how much of
    /// Esri's documented behaviour does it support?". A <c>stub</c> operation on an
    /// in-release route correctly reads <c>status: stub</c> with
    /// <c>capabilityMaturity: [implemented]</c> — the route ships, its backing model does
    /// not. Naming it <c>maturity</c> invited exactly that misreading.
    /// </para>
    /// </summary>
    public string[] CapabilityMaturity { get; init; } = [];

    /// <summary>Extension metadata when the operation has no Esri equivalent. Authored.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HonuaExtension? HonuaExtension { get; init; }

    /// <summary>Gap prose. Authored.</summary>
    public string Notes { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(ParityMatrix))]
[JsonSerializable(typeof(ParityJudgment))]
internal sealed partial class ParityMatrixJsonContext : JsonSerializerContext
{
}
