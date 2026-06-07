// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Stac.Models;

/// <summary>
/// Constants for the STAC API implementation.
/// </summary>
internal static class StacConstants
{
    /// <summary>
    /// STAC specification version implemented.
    /// </summary>
    public const string StacVersion = "1.0.0";

    /// <summary>
    /// Catalog identifier.
    /// </summary>
    public const string CatalogId = "honua-stac-catalog";

    /// <summary>
    /// Default limit for search results.
    /// </summary>
    public const int DefaultSearchLimit = 10;

    /// <summary>
    /// Maximum allowed limit for search results.
    /// </summary>
    public const int MaxSearchLimit = 10_000;

    /// <summary>
    /// STAC API conformance URIs.
    /// </summary>
    internal static class Conformance
    {
        public const string Core = "https://api.stacspec.org/v1.0.0/core";
        public const string ItemSearch = "https://api.stacspec.org/v1.0.0/item-search";
        public const string OgcApiFeatures = "https://api.stacspec.org/v1.0.0/ogcapi-features";
        public const string Collections = "https://api.stacspec.org/v1.0.0/collections";
        public const string FieldsExtension = "https://api.stacspec.org/v1.0.0/item-search#fields";
        public const string SortExtension = "https://api.stacspec.org/v1.0.0/item-search#sort";
        public const string FilterExtension = "https://api.stacspec.org/v1.0.0/item-search#filter";
    }

    /// <summary>
    /// STAC-specific link relation types.
    /// </summary>
    internal static class StacRelations
    {
        public const string Root = "root";
        public const string Parent = "parent";
        public const string Child = "child";
        public const string Item = "item";
        public const string Items = "items";
        public const string Search = "search";
    }

    /// <summary>
    /// Allowed query parameters by endpoint.
    /// </summary>
    internal static class AllowedQueryParameters
    {
        public static readonly HashSet<string> Catalog = new(StringComparer.OrdinalIgnoreCase) { "f" };

        public static readonly HashSet<string> Collections = new(StringComparer.OrdinalIgnoreCase) { "f" };

        public static readonly HashSet<string> Items = new(StringComparer.OrdinalIgnoreCase)
        {
            "f", "limit", "offset", "bbox", "datetime"
        };

        public static readonly HashSet<string> Item = new(StringComparer.OrdinalIgnoreCase) { "f" };

        public static readonly HashSet<string> SearchGet = new(StringComparer.OrdinalIgnoreCase)
        {
            "f", "limit", "offset", "bbox", "datetime", "collections", "ids",
            "intersects", "fields", "sortby", "filter", "filter-lang", "filter-crs"
        };
    }
}
