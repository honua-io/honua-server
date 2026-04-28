// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Rendering;

namespace Honua.Server.Features.Infrastructure.Styling.Sld;

/// <summary>
/// Result of converting an SLD document into MapLibre style layers.
/// </summary>
internal sealed record SldConversionResult
{
    public required MapLibreStyleLayer[] Layers { get; init; }

    public required SldConversionDiagnostic[] Diagnostics { get; init; }

    public required SldVersion DetectedVersion { get; init; }

    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == SldDiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Result of converting MapLibre layers back to an SLD document.
/// </summary>
internal sealed record SldExportResult
{
    public required string SldXml { get; init; }

    public required SldConversionDiagnostic[] Diagnostics { get; init; }
}

/// <summary>
/// Structured diagnostic emitted by SLD import/export. Surfaces unsupported
/// or lossy constructs so callers can choose how to react.
/// </summary>
public sealed record SldConversionDiagnostic
{
    /// <summary>
    /// Severity of the diagnostic.
    /// </summary>
    [JsonPropertyName("severity")]
    public required SldDiagnosticSeverity Severity { get; init; }

    /// <summary>
    /// Construct name that triggered the diagnostic (e.g. <c>VendorOption</c>,
    /// <c>ExternalGraphic</c>, <c>OgcFunction</c>).
    /// </summary>
    [JsonPropertyName("construct")]
    public required string Construct { get; init; }

    /// <summary>
    /// Human-readable explanation. Must not include raw exception messages.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Optional rule name for diagnostics emitted while processing a specific rule.
    /// </summary>
    [JsonPropertyName("ruleName")]
    public string? RuleName { get; init; }
}

/// <summary>
/// Severity levels for SLD diagnostics. <see cref="Error"/> aborts import; <see cref="Warning"/>
/// is informational and accompanies a successful conversion.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SldDiagnosticSeverity>))]
public enum SldDiagnosticSeverity
{
    Warning,
    Error
}
