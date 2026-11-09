// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Styling.Domain;

public sealed class StyleSuggestion
{
    public required int LayerId { get; init; }

    public required MetadataV2GeometryType GeometryType { get; init; }

    public FieldSuggestion? SuggestedField { get; init; }

    public ClassificationResult? Classification { get; init; }

    public required string PaletteName { get; init; }

    public required string[] PaletteColors { get; init; }

    public required LegendInfo Legend { get; init; }

    public required IReadOnlyList<string> Observations { get; init; }

    public required HonuaEdition Edition { get; init; }
}

public sealed class FieldSuggestion
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public required string Reason { get; init; }

    public required FieldProfile Profile { get; init; }
}
