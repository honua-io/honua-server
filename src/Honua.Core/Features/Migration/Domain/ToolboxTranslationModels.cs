// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// SDK-emitted translation manifest for one ArcGIS toolbox (<c>.pyt</c>, <c>.tbx</c>, or
/// <c>.atbx</c>). The scanner/translator (honua-sdk-python <c>honua-migrate</c>) parses the
/// toolbox source and proposes, per tool, a mapping onto an existing native Honua process.
/// The server validates the proposal against the canonical process catalog and returns a
/// <see cref="ToolboxTranslationReport"/>; it never parses toolbox sources or emulates
/// arcpy execution (#2145).
/// </summary>
public sealed record ToolboxTranslationManifest
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = ToolboxTranslationArtifacts.ManifestKind;

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Toolbox name as declared in the source toolbox.
    /// </summary>
    public required string ToolboxName { get; init; }

    /// <summary>
    /// Source toolbox format. One of <see cref="ToolboxSourceFormats"/>.
    /// </summary>
    public required string SourceFormat { get; init; }

    /// <summary>
    /// Optional operator-visible source label (for example a redacted file name). The SDK is
    /// responsible for redacting local paths and credentials before submission.
    /// </summary>
    public string? SourceLabel { get; init; }

    /// <summary>
    /// Translated tool descriptors proposed by the scanner.
    /// </summary>
    public ToolboxToolDescriptor[] Tools { get; init; } = [];
}

/// <summary>
/// One toolbox tool as translated by the SDK scanner: either a proposed mapping onto a
/// native Honua process, or an explicitly untranslatable tool
/// (<see cref="TargetProcessId"/> is <c>null</c>).
/// </summary>
public sealed record ToolboxToolDescriptor
{
    /// <summary>
    /// Tool name as declared in the source toolbox.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Optional human-readable tool label from the toolbox metadata.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Canonical Honua process identifier the scanner proposes as the native executor
    /// (for example <c>geometry.buffer</c>). <c>null</c> when the scanner found no native
    /// mapping; the tool is then reported unsupported rather than stubbed as executable.
    /// </summary>
    public string? TargetProcessId { get; init; }

    /// <summary>
    /// Proposed source-parameter to canonical-parameter mappings.
    /// </summary>
    public ToolboxParameterMapping[] ParameterMappings { get; init; } = [];

    /// <summary>
    /// Constructs the scanner detected that cannot be translated (custom Python execution,
    /// <c>arcpy.env</c> dependencies, unsupported parameter/data types, license checks).
    /// Each entry becomes an explicit unsupported-report issue instead of being silently
    /// dropped.
    /// </summary>
    public string[] UnsupportedConstructs { get; init; } = [];
}

/// <summary>
/// Maps a single source toolbox parameter onto a canonical process parameter.
/// </summary>
public sealed record ToolboxParameterMapping
{
    /// <summary>
    /// Parameter name as declared in the source tool.
    /// </summary>
    public required string SourceName { get; init; }

    /// <summary>
    /// Canonical process parameter name this source parameter maps to.
    /// </summary>
    public required string TargetParameter { get; init; }

    /// <summary>
    /// Optional original Esri data type (for example <c>GPFeatureLayer</c>), informational
    /// only.
    /// </summary>
    public string? SourceDataType { get; init; }
}

/// <summary>
/// Server-authoritative validation report for a <see cref="ToolboxTranslationManifest"/>.
/// Round-trips the canonical parameter signature for every translatable tool and captures
/// untranslatable constructs in explicit per-tool issues.
/// </summary>
public sealed record ToolboxTranslationReport
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = ToolboxTranslationArtifacts.ReportKind;

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Toolbox name echoed from the manifest.
    /// </summary>
    public required string ToolboxName { get; init; }

    /// <summary>
    /// Source toolbox format echoed from the manifest, normalized to lower case.
    /// </summary>
    public required string SourceFormat { get; init; }

    /// <summary>
    /// Per-classification tool counts.
    /// </summary>
    public required ToolboxTranslationSummary Summary { get; init; }

    /// <summary>
    /// Per-tool translation outcomes, in manifest order.
    /// </summary>
    public ToolboxToolTranslation[] Tools { get; init; } = [];
}

/// <summary>
/// Aggregated tool counts for a <see cref="ToolboxTranslationReport"/>.
/// </summary>
public sealed record ToolboxTranslationSummary
{
    /// <summary>
    /// Total number of tools in the manifest.
    /// </summary>
    public required int ToolCount { get; init; }

    /// <summary>
    /// Tools whose full parameter signature round-trips onto a native process.
    /// </summary>
    public required int TranslatedCount { get; init; }

    /// <summary>
    /// Tools that resolve to a native process but carry unsupported parameters or
    /// constructs.
    /// </summary>
    public required int PartiallyTranslatedCount { get; init; }

    /// <summary>
    /// Tools with no executable native mapping.
    /// </summary>
    public required int UnsupportedCount { get; init; }
}

/// <summary>
/// Validation outcome for a single toolbox tool.
/// </summary>
public sealed record ToolboxToolTranslation
{
    /// <summary>
    /// Tool name echoed from the manifest.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Translation classification. One of <see cref="ToolboxToolClassifications"/>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Canonical process identifier the tool resolves to, when the proposed target exists in
    /// the process catalog. Populated for diagnostic value even when the tool is classified
    /// unsupported (for example because a required parameter is unmapped); <c>null</c> when
    /// no catalog process resolves.
    /// </summary>
    public string? ProcessId { get; init; }

    /// <summary>
    /// Round-tripped canonical parameter signature for every valid mapping.
    /// </summary>
    public ToolboxParameterBinding[] ParameterBindings { get; init; } = [];

    /// <summary>
    /// Explicit unsupported-report entries. Empty only for fully translated tools.
    /// </summary>
    public ToolboxTranslationIssue[] Issues { get; init; } = [];
}

/// <summary>
/// Round-tripped canonical signature for one validated parameter mapping.
/// </summary>
public sealed record ToolboxParameterBinding
{
    /// <summary>
    /// Source toolbox parameter name.
    /// </summary>
    public required string SourceName { get; init; }

    /// <summary>
    /// Canonical process parameter name (catalog casing).
    /// </summary>
    public required string TargetParameter { get; init; }

    /// <summary>
    /// Canonical parameter value type name (for example <c>Wkb</c>, <c>Srid</c>,
    /// <c>FloatingPoint</c>).
    /// </summary>
    public required string ValueType { get; init; }

    /// <summary>
    /// Whether the canonical parameter is required.
    /// </summary>
    public required bool Required { get; init; }
}

/// <summary>
/// One explicit unsupported-report entry for a toolbox tool.
/// </summary>
public sealed record ToolboxTranslationIssue
{
    /// <summary>
    /// Stable machine-readable issue code. One of <see cref="ToolboxTranslationIssueCodes"/>.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable explanation of why the construct cannot be translated.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Parameter name the issue applies to, when parameter-scoped.
    /// </summary>
    public string? ParameterName { get; init; }
}

/// <summary>
/// Stable artifact identities for the toolbox translation lane. Callers must submit a
/// manifest whose <see cref="ToolboxTranslationManifest.ArtifactKind"/> and
/// <see cref="ToolboxTranslationManifest.ArtifactVersion"/> match a supported identity;
/// an incompatible payload is rejected rather than reinterpreted under the v1 contract.
/// </summary>
public static class ToolboxTranslationArtifacts
{
    /// <summary>Stable artifact kind for the inbound translation manifest.</summary>
    public const string ManifestKind = "honua.migration.toolbox-translation";

    /// <summary>Stable artifact kind for the server-authoritative report.</summary>
    public const string ReportKind = "honua.migration.toolbox-translation-report";

    /// <summary>Manifest schema versions this server understands.</summary>
    public static readonly IReadOnlyList<string> SupportedManifestVersions = ["1.0"];
}

/// <summary>
/// Known source toolbox formats accepted by the translation lane.
/// </summary>
public static class ToolboxSourceFormats
{
    /// <summary>Python toolbox source (<c>.pyt</c>).</summary>
    public const string Pyt = "pyt";

    /// <summary>Binary toolbox (<c>.tbx</c>).</summary>
    public const string Tbx = "tbx";

    /// <summary>ArcGIS Pro toolbox (<c>.atbx</c>).</summary>
    public const string Atbx = "atbx";

    /// <summary>All known formats, for validation messages.</summary>
    public static readonly IReadOnlyList<string> All = [Pyt, Tbx, Atbx];
}

/// <summary>
/// Classification values for <see cref="ToolboxToolTranslation.Classification"/>.
/// </summary>
public static class ToolboxToolClassifications
{
    /// <summary>The full parameter signature round-trips onto a native process.</summary>
    public const string Translated = "translated";

    /// <summary>
    /// The tool resolves to an executable native process, but some source parameters or
    /// constructs are unsupported and explicitly reported.
    /// </summary>
    public const string PartiallyTranslated = "partially-translated";

    /// <summary>The tool has no executable native mapping and must not be presented as executable.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Stable issue codes for <see cref="ToolboxTranslationIssue.Code"/>.
/// </summary>
public static class ToolboxTranslationIssueCodes
{
    /// <summary>The scanner proposed no native process mapping for the tool.</summary>
    public const string NoNativeExecutor = "no-native-executor";

    /// <summary>The proposed target process id does not exist in the canonical process catalog.</summary>
    public const string UnknownProcess = "unknown-process";

    /// <summary>A mapping targets a parameter the canonical process does not declare.</summary>
    public const string UnknownTargetParameter = "unknown-target-parameter";

    /// <summary>Two mappings target the same canonical parameter.</summary>
    public const string DuplicateTargetParameter = "duplicate-target-parameter";

    /// <summary>A required canonical parameter with no default is not covered by any mapping.</summary>
    public const string MissingRequiredParameter = "missing-required-parameter";

    /// <summary>A scanner-declared source construct cannot be translated.</summary>
    public const string UnsupportedConstruct = "unsupported-construct";

    /// <summary>
    /// The canonical plan validator would reject this mapping because it satisfies no
    /// member of a conditionally-required input group (for example the
    /// mutually-substitutable <c>source</c>/<c>layerId</c>/<c>rasterId</c> raster inputs),
    /// even though every statically-<c>Required</c> parameter is mapped.
    /// </summary>
    public const string UnsatisfiedConditionalInputs = "unsatisfied-conditional-inputs";

    /// <summary>
    /// The mapping leaves a parameter both unmapped and without a declared default, so its
    /// value is undetermined at translation time. Branch-dependent requirements keyed on a
    /// caller-supplied discriminator (for example <c>analytics.cluster-managed</c>, where
    /// <c>k</c> is required only when <c>algorithm=kmeans</c>) cannot be proven for every
    /// branch, so the tool is reported as reviewable rather than certified executable.
    /// </summary>
    public const string UnverifiableConditionalBranches = "unverifiable-conditional-branches";

    /// <summary>
    /// The target process cannot be dispatched as a job at all, whatever the parameters:
    /// it runs only through a synchronous protocol surface, so OGC Processes and GPServer
    /// submission reject it. Translated tools execute through the canonical job runtime,
    /// so such a target is never executable from a toolbox.
    /// </summary>
    public const string ProcessNotJobExecutable = "process-not-job-executable";
}
