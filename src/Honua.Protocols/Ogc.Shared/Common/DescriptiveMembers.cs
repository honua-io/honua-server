// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Crosswalk;

namespace Honua.Protocols.Ogc.Common;

// The theme and language objects OGC API Records defines and the STAC themes and language extensions reuse.
public sealed record ThemeMember
{
    [JsonPropertyName("concepts")]
    public required ImmutableArray<ThemeConcept> Concepts { get; init; }

    [JsonPropertyName("scheme")]
    public required string Scheme { get; init; }
}

public sealed record ThemeConcept
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

public sealed record LanguageMember
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }
}

public static class DescriptiveMembers
{
    public static ImmutableArray<string>? Keywords(IReadOnlyList<string> keywords)
        => keywords.Count == 0 ? null : [.. keywords];

    public static ImmutableArray<ThemeMember>? Themes(IReadOnlyList<string> themes)
    {
        var schemes = MetadataCrosswalk.ThemeSchemes(themes);
        return schemes.Count == 0
            ? null
            : [.. schemes.Select(scheme => new ThemeMember
            {
                Scheme = scheme.Scheme,
                Concepts = [.. scheme.Concepts.Select(concept => new ThemeConcept { Id = concept })],
            })];
    }

    public static LanguageMember? Language(string? language)
        => string.IsNullOrWhiteSpace(language) ? null : new LanguageMember { Code = language.Trim() };
}
