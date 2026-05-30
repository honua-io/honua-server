// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Styling.Sld;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Successful response payload for SLD import.
/// </summary>
public sealed class SldImportResponse
{
    /// <summary>
    /// SLD specification version detected during parsing (e.g. <c>Sld10</c>, <c>Sld11</c>).
    /// </summary>
    public string DetectedVersion { get; init; } = string.Empty;

    /// <summary>
    /// Number of MapLibre layers produced by the conversion.
    /// </summary>
    public int LayerCount { get; init; }

    /// <summary>
    /// Stored MapLibre style document.
    /// </summary>
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// Conversion diagnostics. Warnings are informational; errors abort import.
    /// </summary>
    public IReadOnlyList<SldConversionDiagnostic> Diagnostics { get; init; } = Array.Empty<SldConversionDiagnostic>();
}

/// <summary>
/// Response payload returned with HTTP 422 when error-severity diagnostics block import.
/// </summary>
public sealed class SldImportFailureResponse
{
    /// <summary>
    /// Detected SLD version, if any (parsing may have succeeded but conversion failed).
    /// </summary>
    public string? DetectedVersion { get; init; }

    /// <summary>
    /// Conversion diagnostics including the error-severity entries that blocked import.
    /// </summary>
    public IReadOnlyList<SldConversionDiagnostic> Diagnostics { get; init; } = Array.Empty<SldConversionDiagnostic>();
}
