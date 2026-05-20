// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Structured diagnostic describing how a single source property maps (or fails to map) to the
/// target catalog table. Surfaced in <see cref="OgcApiFeaturesImportResult.MappingDiagnostics"/>
/// so operators can review schema drift before re-running an import.
/// </summary>
/// <remarks>
/// <para>
/// The importer emits at most one diagnostic per source property. Properties that map cleanly
/// (identical name + compatible type) are <b>not</b> reported — only the ones that need operator
/// attention.
/// </para>
/// <para>
/// Classification rules (see <see cref="OgcApiFeaturesSchemaMappingClassification"/>):
/// </para>
/// <list type="bullet">
///   <item><description><b>Automated:</b> reserved for the no-diagnostic case (clean match).</description></item>
///   <item><description><b>Assisted:</b> name match with a widening conversion (e.g. <c>int</c> → <c>bigint</c>,
///     <c>varchar(N)</c> → <c>text</c>). Importer proceeds; emitted as informational.</description></item>
///   <item><description><b>ManualReview:</b> name match with a narrowing conversion (e.g. <c>bigint</c> → <c>int</c>,
///     <c>text</c> → <c>varchar(N)</c>). Importer proceeds; emitted as warning.</description></item>
///   <item><description><b>Unsupported:</b> the target table has no column matching the source property name. The
///     source value cannot be projected; emitted as error.</description></item>
/// </list>
/// </remarks>
public sealed record OgcApiFeaturesSchemaMappingDiagnostic
{
    /// <summary>
    /// Source property name as it appears in the OGC API Features JSON schema or in the first-page
    /// feature properties when no schema document is advertised.
    /// </summary>
    public required string PropertyName { get; init; }

    /// <summary>
    /// Source property type, as advertised by the JSON schema (<c>integer</c>, <c>string</c>, etc.)
    /// or inferred from the first feature instance.
    /// </summary>
    public required string SourceType { get; init; }

    /// <summary>
    /// Target column type from the catalog table, or <c>null</c> when the target has no matching
    /// column (<see cref="Classification"/> = <see cref="OgcApiFeaturesSchemaMappingClassification.Unsupported"/>).
    /// </summary>
    public string? TargetColumnType { get; init; }

    /// <summary>
    /// Classification of the mapping outcome.
    /// </summary>
    public required OgcApiFeaturesSchemaMappingClassification Classification { get; init; }

    /// <summary>
    /// Severity hint for downstream tooling. <c>info</c> for assisted conversions, <c>warning</c>
    /// for manual-review, <c>error</c> for unsupported.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Human-readable reason summarizing the diagnostic so it can be rendered verbatim in the
    /// operator dashboard.
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Classification taxonomy for schema mapping diagnostics emitted by the OGC API Features
/// collection importer.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OgcApiFeaturesSchemaMappingClassification>))]
public enum OgcApiFeaturesSchemaMappingClassification
{
    /// <summary>
    /// Identical name + compatible type. The importer never emits a diagnostic for this case; the
    /// enum value exists only so the classification taxonomy is exhaustive for downstream tooling.
    /// </summary>
    Automated = 0,

    /// <summary>
    /// Name match with a widening conversion (e.g. <c>int</c> → <c>bigint</c>, <c>varchar(N)</c> →
    /// <c>text</c>). Emitted as informational.
    /// </summary>
    Assisted = 1,

    /// <summary>
    /// Name match with a narrowing conversion (e.g. <c>bigint</c> → <c>int</c>, <c>text</c> →
    /// <c>varchar(N)</c>). Emitted as warning so an operator can confirm before re-running.
    /// </summary>
    ManualReview = 2,

    /// <summary>
    /// No target column matches the source property name. Emitted as error.
    /// </summary>
    Unsupported = 3
}
