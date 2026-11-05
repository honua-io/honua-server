// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Admin.Models;

public sealed class StyleSuggestionRequest
{
    public string? PreferredField { get; init; }

    public string? PreferredMethod { get; init; }

    public string? PreferredPalette { get; init; }

    public int? ClassCount { get; init; }
}

public sealed class StyleSuggestionResponse
{
    public JsonElement? MapLibreStyle { get; init; }

    public JsonElement? DrawingInfo { get; init; }

    public StyleSuggestionLegend? Legend { get; init; }

    public StyleSuggestionFieldInfo? SuggestedField { get; init; }

    public string? ClassificationMethod { get; init; }

    public string? PaletteName { get; init; }

    public string[]? Observations { get; init; }

    public string? Edition { get; init; }
}

public sealed class StyleSuggestionLegend
{
    public string? Title { get; init; }

    public StyleSuggestionLegendEntry[]? Entries { get; init; }
}

public sealed class StyleSuggestionLegendEntry
{
    public string? Label { get; init; }

    public string? Color { get; init; }

    public double? MinValue { get; init; }

    public double? MaxValue { get; init; }
}

public sealed class StyleSuggestionFieldInfo
{
    public string? Name { get; init; }

    public string? Type { get; init; }

    public string? Reason { get; init; }
}
