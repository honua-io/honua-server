// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Converts SLD/SE XML documents to canonical MapLibre style JSON. Implementations
/// must reject malformed and unsafe XML cleanly and surface unsupported constructs
/// as warnings rather than dropping them silently.
/// </summary>
public interface ISldStyleConverter
{
    /// <summary>
    /// Converts an SLD XML document to MapLibre style JSON.
    /// </summary>
    /// <param name="sldXml">Raw SLD/SE XML document.</param>
    /// <returns>Conversion result, including diagnostics and (on success) a MapLibre layer-array JSON string.</returns>
    SldStyleConversionResult Convert(string sldXml);
}

/// <summary>
/// Outcome of an SLD-to-MapLibre conversion attempt.
/// </summary>
/// <param name="MapLibreLayersJson">JSON array of MapLibre layer objects (canonical schema). <c>null</c> when the conversion failed before producing layers.</param>
/// <param name="DetectedSldVersion">Detected SLD version (e.g. <c>Sld10</c> or <c>Sld11</c>); <c>null</c> when parsing failed.</param>
/// <param name="Warnings">Human-readable warning messages; the conversion still produced layers.</param>
/// <param name="Errors">Human-readable error messages that prevented conversion (e.g. malformed XML, no convertible symbolizers).</param>
public sealed record SldStyleConversionResult(
    string? MapLibreLayersJson,
    string? DetectedSldVersion,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// True when conversion produced no layers or hit a fatal error.
    /// </summary>
    public bool HasErrors => Errors.Count > 0 || MapLibreLayersJson == null;
}
