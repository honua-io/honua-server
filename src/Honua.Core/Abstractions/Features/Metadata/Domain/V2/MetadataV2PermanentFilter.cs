// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

using Honua.Core.Serialization;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2PermanentFilter : ExtensibleJsonRecord
{
    [JsonPropertyName("expression")]
    public string Expression { get; init; } = string.Empty;

    // An unknown filter language fails the query rather than skipping the filter.
    [JsonPropertyName("language")]
    public string Language { get; init; } = MetadataV2PermanentFilterLanguages.GeoServicesSql;
}

public static class MetadataV2PermanentFilterLanguages
{
    public const string GeoServicesSql = "arcgis-sql";

    public const string Cql2Text = "cql2-text";

    public const string Cql2Json = "cql2-json";

    /// <summary>Resolves saved filter language names and legacy aliases; unknown names fail closed.</summary>
    public static bool TryResolve(string? language, out FilterLanguage filterLanguage)
    {
        filterLanguage = FilterLanguage.GeoServicesSql;
        var normalized = (language ?? GeoServicesSql)
            .Trim()
            .ToLowerInvariant();

        switch (normalized)
        {
            case GeoServicesSql:
            case "arcgis":
            case "geoservices-sql":
                filterLanguage = FilterLanguage.GeoServicesSql;
                return true;
            case Cql2Text:
            case "cql2":
                filterLanguage = FilterLanguage.Cql2Text;
                return true;
            case Cql2Json:
                filterLanguage = FilterLanguage.Cql2Json;
                return true;
            default:
                return false;
        }
    }
}
