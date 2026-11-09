// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Stac.Models;

internal static class StacConstants
{
    public const string StacVersion = "1.0.0";

    public const string CatalogId = "honua-stac-catalog";

    public const int DefaultSearchLimit = 10;

    public const int MaxSearchLimit = 10_000;

    internal static class Conformance
    {
        public const string Core = "https://api.stacspec.org/v1.0.0/core";
        public const string ItemSearch = "https://api.stacspec.org/v1.0.0/item-search";
        public const string OgcApiFeatures = "https://api.stacspec.org/v1.0.0/ogcapi-features";
        public const string Collections = "https://api.stacspec.org/v1.0.0/collections";
        public const string FieldsExtension = "https://api.stacspec.org/v1.0.0/item-search#fields";
        public const string SortExtension = "https://api.stacspec.org/v1.0.0/item-search#sort";
        public const string FilterExtension = "https://api.stacspec.org/v1.0.0/item-search#filter";

        // OGC API - Features Part 1 conformance URIs required by STAC API - Features.
        public const string OgcFeaturesCore = "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core";
        public const string OgcFeaturesOas30 = "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30";
        public const string OgcFeaturesGeoJson = "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson";

        // OGC API - Features Part 3 / CQL2 conformance URIs required by the Filter Extension.
        public const string OgcFeaturesFilter = "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter";
        public const string Cql2BasicCql2 = "http://www.opengis.net/spec/cql2/1.0/conf/basic-cql2";
        public const string Cql2Text = "http://www.opengis.net/spec/cql2/1.0/conf/cql2-text";
        public const string Cql2Json = "http://www.opengis.net/spec/cql2/1.0/conf/cql2-json";
    }

    internal static class StacRelations
    {
        public const string Root = "root";
        public const string Parent = "parent";
        public const string Child = "child";
        public const string Item = "item";
        public const string Items = "items";
        public const string Search = "search";
    }

    internal static class ProviderRoles
    {
        public const string Producer = "producer";
        public const string Host = "host";
    }

    internal static class AllowedQueryParameters
    {
        public static readonly HashSet<string> Catalog = new(StringComparer.OrdinalIgnoreCase) { "f" };

        public static readonly HashSet<string> Collections = new(StringComparer.OrdinalIgnoreCase) { "f" };

        public static readonly HashSet<string> Items = new(StringComparer.OrdinalIgnoreCase)
        {
            "f", "limit", "offset", "bbox", "datetime"
        };

        public static readonly HashSet<string> SearchGet = new(StringComparer.OrdinalIgnoreCase)
        {
            // "offset" is a Honua extension kept for backward compatibility.
            // "next" carries the offset:N cursor. "token" stays allowed so an
            // ArcGIS portal credential on the query string is not rejected.
            "f", "limit", "offset", "token", "next", "bbox", "datetime", "collections", "ids",
            "intersects", "fields", "sortby", "filter", "filter-lang", "filter-crs"
        };
    }
}
