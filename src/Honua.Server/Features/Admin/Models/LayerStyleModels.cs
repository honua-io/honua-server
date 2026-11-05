// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Server.Features.Admin.Models;

public sealed class LayerStyleUpdateRequest
{
    public JsonElement? MapLibreStyle { get; init; }

    public JsonElement? DrawingInfo { get; init; }

    public string? ChangedBy { get; init; }

    public string? ChangeSummary { get; init; }
}

public sealed class LayerStyleResponse
{
    public JsonElement? MapLibreStyle { get; init; }

    public JsonElement? DrawingInfo { get; init; }

    public int StyleVersion { get; init; }

    public DateTimeOffset? RevisedAt { get; init; }

    public string? RevisedBy { get; init; }

    public string? ChangeSummary { get; init; }

    public IReadOnlyList<UnsupportedSymbolizerInfo>? UnsupportedSymbolizers { get; init; }
}
