// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Reporting.Domain;

/// <summary>
/// Output formats supported by analysis report renderers.
/// </summary>
public enum AnalysisReportFormat
{
    /// <summary>
    /// CommonMark Markdown body (no front matter).
    /// </summary>
    Markdown = 0,

    /// <summary>
    /// Self-contained HTML document with inline CSS and embedded SVG charts.
    /// </summary>
    Html = 1
}

/// <summary>
/// Narrative path that produced the textual blocks in a rendered report.
/// </summary>
public enum NarrativeMode
{
    /// <summary>
    /// All narrative blocks were produced by the deterministic provider.
    /// </summary>
    Deterministic = 0,

    /// <summary>
    /// One or more narrative blocks were produced by the LLM provider on top
    /// of the deterministic scaffolding.
    /// </summary>
    LlmAssisted = 1,

    /// <summary>
    /// The LLM provider failed or timed out and the renderer fell back to the
    /// deterministic narrative path.
    /// </summary>
    FallbackFromLlmError = 2
}

/// <summary>
/// Discriminator for <see cref="AnalysisReportSection"/> subtypes. Carried as a
/// JSON-serializable string so polymorphic round-trips do not depend on .NET
/// type identity.
/// </summary>
public static class AnalysisReportSectionKinds
{
    /// <summary>Heading section.</summary>
    public const string Heading = "heading";

    /// <summary>Paragraph of plain text.</summary>
    public const string Paragraph = "paragraph";

    /// <summary>Single key/value metric section.</summary>
    public const string KeyMetric = "key-metric";

    /// <summary>Tabular data with column headers.</summary>
    public const string Table = "table";

    /// <summary>Chart data rendered server-side as inline SVG.</summary>
    public const string Chart = "chart";

    /// <summary>Pointer to a related <c>honua://map-packages/{id}</c> resource.</summary>
    public const string MapEmbed = "map-embed";

    /// <summary>Narrative block authored by the narrative provider.</summary>
    public const string Narrative = "narrative";

    /// <summary>Footer that summarizes provenance.</summary>
    public const string ProvenanceFooter = "provenance-footer";
}
