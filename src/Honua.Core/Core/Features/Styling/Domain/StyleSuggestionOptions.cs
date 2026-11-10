// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Styling.Domain;

public sealed class StyleSuggestionOptions
{
    public string? PreferredField { get; init; }

    public ClassificationMethod? PreferredMethod { get; init; }

    public string? PreferredPalette { get; init; }

    public int ClassCount { get; init; } = 5;

    public HonuaEdition Edition { get; init; } = HonuaEdition.Community;
}
