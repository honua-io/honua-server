// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload for updating a layer style.
/// </summary>
public sealed class LayerStyleUpdateRequest
{
    /// <summary>
    /// MapLibre style JSON (canonical format).
    /// </summary>
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (import/compat).
    /// </summary>
    public JsonElement? DrawingInfo { get; init; }

    /// <summary>
    /// Optional author or source identifier captured for the new revision.
    /// </summary>
    public string? ChangedBy { get; init; }

    /// <summary>
    /// Optional operator-supplied description of the change (max 1000 characters).
    /// </summary>
    public string? ChangeSummary { get; init; }
}

/// <summary>
/// Response payload containing layer styles.
/// </summary>
public sealed class LayerStyleResponse
{
    /// <summary>
    /// MapLibre style JSON (canonical format).
    /// </summary>
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (cached conversion or import).
    /// </summary>
    public JsonElement? DrawingInfo { get; init; }

    /// <summary>
    /// Monotonic style revision counter incremented on every canonical update.
    /// </summary>
    public int StyleVersion { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent canonical update; null until the first revision.
    /// </summary>
    public DateTimeOffset? RevisedAt { get; init; }

    /// <summary>
    /// Author or source identifier captured for the most recent revision.
    /// </summary>
    public string? RevisedBy { get; init; }

    /// <summary>
    /// Operator-supplied description of the most recent revision.
    /// </summary>
    public string? ChangeSummary { get; init; }

    /// <summary>
    /// Symbolizers from the submitted payload that could not be losslessly translated.
    /// Empty when the entire payload was supported.  Codes match
    /// <see cref="StyleErrorCodes"/>.
    /// </summary>
    public IReadOnlyList<UnsupportedSymbolizerInfo>? UnsupportedSymbolizers { get; init; }
}
