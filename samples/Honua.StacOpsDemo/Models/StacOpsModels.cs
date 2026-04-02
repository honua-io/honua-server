using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.StacOpsDemo.Models;

internal enum SignalLevel
{
    Pass,
    Warn,
    Fail,
    Info
}

internal sealed record StatusCard(
    string Title,
    SignalLevel Level,
    string Summary,
    string Detail);

internal sealed record CollectionSampleItem(
    string Id,
    string? Datetime,
    string Summary,
    string[] ObservedPrefixes);

internal sealed record CollectionInsight(
    string Id,
    string Title,
    SignalLevel Level,
    string Verdict,
    string Detail,
    long? NumberMatched,
    int SampleCount,
    bool HasItemsLink,
    bool SupportsPaging,
    int MissingDatetimeCount,
    string? LatestObservedDatetime,
    string? AdvertisedLatestDatetime,
    string? License,
    string[] Keywords,
    string[] DeclaredExtensions,
    string[] DeclaredExtensionKeys,
    string[] ObservedPrefixes,
    string[] ObservedExtensionKeys,
    bool CachedTemporalDriftDetected,
    string[] DriftNotes,
    string? ETag,
    string? EoMetric,
    string? ProjectionMetric,
    string? ViewMetric,
    CollectionSampleItem[] SampleItems,
    string[] ObservedProperties);

internal sealed record ExtensionInsight(
    string Key,
    string Label,
    SignalLevel Level,
    string DeclaredSummary,
    string ObservedSummary,
    string Verdict,
    string Detail,
    string MetricSummary);

internal sealed record QueryProbeInsight(
    string Label,
    SignalLevel Level,
    string Verdict,
    string Detail,
    string RequestPath);

internal sealed record RequestLedgerEntry(
    string Method,
    string Url,
    int StatusCode,
    long DurationMs,
    string? ETag,
    string? CacheControl,
    string? Age,
    string? Note);

internal sealed record DashboardSnapshot(
    string SourceBaseUrl,
    DateTimeOffset GeneratedAt,
    StatusCard[] StatusCards,
    CollectionInsight[] Collections,
    ExtensionInsight[] Extensions,
    QueryProbeInsight[] QueryProbes,
    RequestLedgerEntry[] RequestLedger,
    string[] ConformanceClasses,
    bool StaleMetadataSuspected,
    string[] Notes);

internal sealed record StacLinkDocument
{
    [JsonPropertyName("href")]
    public string? Href { get; init; }

    [JsonPropertyName("rel")]
    public string? Rel { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

internal sealed record StacCatalogDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("conformsTo")]
    public string[]? ConformsTo { get; init; }

    [JsonPropertyName("links")]
    public StacLinkDocument[]? Links { get; init; }
}

internal sealed record StacCollectionsResponseDocument
{
    [JsonPropertyName("collections")]
    public StacCollectionDocument[]? Collections { get; init; }

    [JsonPropertyName("links")]
    public StacLinkDocument[]? Links { get; init; }
}

internal sealed record StacCollectionDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("keywords")]
    public string[]? Keywords { get; init; }

    [JsonPropertyName("stac_extensions")]
    public string[]? StacExtensions { get; init; }

    [JsonPropertyName("extent")]
    public StacExtentDocument? Extent { get; init; }

    [JsonPropertyName("links")]
    public StacLinkDocument[]? Links { get; init; }
}

internal sealed record StacExtentDocument
{
    [JsonPropertyName("temporal")]
    public StacTemporalExtentDocument? Temporal { get; init; }
}

internal sealed record StacTemporalExtentDocument
{
    [JsonPropertyName("interval")]
    public string?[][]? Interval { get; init; }
}

internal sealed record StacItemCollectionDocument
{
    [JsonPropertyName("features")]
    public StacItemDocument[]? Features { get; init; }

    [JsonPropertyName("links")]
    public StacLinkDocument[]? Links { get; init; }

    [JsonPropertyName("numberMatched")]
    public long? NumberMatched { get; init; }

    [JsonPropertyName("numberReturned")]
    public int? NumberReturned { get; init; }
}

internal sealed record StacItemDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("collection")]
    public string? Collection { get; init; }

    [JsonPropertyName("stac_extensions")]
    public string[]? StacExtensions { get; init; }

    [JsonPropertyName("properties")]
    public JsonElement Properties { get; init; }

    [JsonPropertyName("assets")]
    public JsonElement Assets { get; init; }
}

internal sealed record ProbeEnvelope<T>(
    T? Document,
    RequestLedgerEntry Request,
    string? BodyText,
    bool IsSuccess);
