using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.StacOpsDemo.Models;

namespace Honua.StacOpsDemo.Services;

internal sealed class StacOpsDashboardService(HttpClient httpClient)
{
    private const int CollectionItemsLimit = 2;
    private const int CollectionSearchLimit = 3;
    private const int GlobalSearchLimit = 50;

    private readonly HttpClient _httpClient = httpClient;

    private static readonly ExtensionDescriptor[] KnownExtensions =
    [
        new("eo", "EO", "/eo/"),
        new("proj", "Projection", "/projection/"),
        new("view", "View", "/view/")
    ];

    private static readonly Dictionary<string, ExtensionDescriptor> KnownExtensionsByKey =
        KnownExtensions.ToDictionary(static descriptor => descriptor.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NonRemovableFieldProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime"
    };

    public async Task<DashboardSnapshot> LoadAsync(string? sourceInput, CancellationToken cancellationToken)
    {
        var sourceBaseUrl = ResolveSourceBaseUrl(sourceInput);
        var ledger = new ConcurrentQueue<RequestLedgerEntry>();

        var liveTask = ProbeTextAsync(sourceBaseUrl, "/healthz/live", "Liveness check", ledger, cancellationToken);
        var readyTask = ProbeTextAsync(sourceBaseUrl, "/healthz/ready", "Readiness check", ledger, cancellationToken);
        var catalogTask = ProbeJsonAsync(
            sourceBaseUrl,
            "/stac",
            "Catalog discovery",
            StacOpsJsonContext.Default.StacCatalogDocument,
            ledger,
            cancellationToken);
        var collectionsTask = ProbeJsonAsync(
            sourceBaseUrl,
            "/stac/collections",
            "Collection discovery",
            StacOpsJsonContext.Default.StacCollectionsResponseDocument,
            ledger,
            cancellationToken);

        await Task.WhenAll(liveTask, readyTask, catalogTask, collectionsTask).ConfigureAwait(false);

        var live = await liveTask.ConfigureAwait(false);
        var ready = await readyTask.ConfigureAwait(false);
        var catalog = await catalogTask.ConfigureAwait(false);
        var collections = await collectionsTask.ConfigureAwait(false);

        var collectionDocuments = collections.Document?.Collections ?? [];
        var collectionInsights = await AnalyzeCollectionsAsync(
            sourceBaseUrl,
            collectionDocuments,
            ledger,
            cancellationToken).ConfigureAwait(false);

        var globalSearch = await ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/search?limit={GlobalSearchLimit.ToString(CultureInfo.InvariantCulture)}",
            "Global search sample",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken).ConfigureAwait(false);

        var collectionIds = collectionInsights
            .Select(static collection => collection.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogChildIds = ExtractCatalogChildIds(catalog.Document);
        var globalSearchCollectionIds = ExtractSearchCollectionIds(globalSearch.Document);
        var missingCollectionIds = globalSearchCollectionIds
            .Where(id => !collectionIds.Contains(id))
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var staleCollectionIds = collectionInsights
            .Where(static collection => collection.CachedTemporalDriftDetected)
            .Select(static collection => collection.Id)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var staleMetadataSuspected = missingCollectionIds.Length > 0 || staleCollectionIds.Length > 0;

        var queryProbes = await BuildQueryProbesAsync(
            sourceBaseUrl,
            collectionInsights,
            staleMetadataSuspected,
            missingCollectionIds,
            staleCollectionIds,
            ledger,
            cancellationToken).ConfigureAwait(false);

        var extensionInsights = BuildExtensionInsights(collectionInsights);
        var statusCards = BuildStatusCards(
            live,
            ready,
            catalog,
            collections,
            collectionInsights,
            queryProbes,
            catalogChildIds,
            collectionIds,
            staleMetadataSuspected,
            missingCollectionIds,
            staleCollectionIds);

        var notes = BuildNotes(
            sourceBaseUrl,
            staleMetadataSuspected,
            missingCollectionIds,
            staleCollectionIds,
            collectionInsights,
            queryProbes);

        return new DashboardSnapshot(
            SourceBaseUrl: sourceBaseUrl,
            GeneratedAt: DateTimeOffset.UtcNow,
            StatusCards: statusCards,
            Collections: collectionInsights,
            Extensions: extensionInsights,
            QueryProbes: queryProbes,
            RequestLedger: ledger.ToArray(),
            ConformanceClasses: catalog.Document?.ConformsTo ?? [],
            StaleMetadataSuspected: staleMetadataSuspected,
            Notes: notes);
    }

    private async Task<CollectionInsight[]> AnalyzeCollectionsAsync(
        string sourceBaseUrl,
        StacCollectionDocument[] collections,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var concurrencyGate = new SemaphoreSlim(4);
        var tasks = collections.Select(async collection =>
        {
            await concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await AnalyzeCollectionAsync(sourceBaseUrl, collection, ledger, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        var insights = await Task.WhenAll(tasks).ConfigureAwait(false);
        return insights
            .OrderBy(static collection => collection.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<CollectionInsight> AnalyzeCollectionAsync(
        string sourceBaseUrl,
        StacCollectionDocument collection,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var collectionId = collection.Id ?? "unknown";
        var escapedCollectionId = Uri.EscapeDataString(collectionId);

        var detailTask = ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/collections/{escapedCollectionId}",
            $"Collection detail {collectionId}",
            StacOpsJsonContext.Default.StacCollectionDocument,
            ledger,
            cancellationToken);
        var itemsTask = ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/collections/{escapedCollectionId}/items?limit={CollectionItemsLimit.ToString(CultureInfo.InvariantCulture)}",
            $"Collection items {collectionId}",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken);
        var searchTask = ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/search?collections={escapedCollectionId}&limit={CollectionSearchLimit.ToString(CultureInfo.InvariantCulture)}",
            $"Collection search sample {collectionId}",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken);

        await Task.WhenAll(detailTask, itemsTask, searchTask).ConfigureAwait(false);

        var detail = await detailTask.ConfigureAwait(false);
        var items = await itemsTask.ConfigureAwait(false);
        var search = await searchTask.ConfigureAwait(false);

        var detailDocument = detail.Document ?? collection;
        var itemsDocument = items.Document;
        var searchDocument = search.Document;

        var nextLink = itemsDocument?.Links?.FirstOrDefault(static link =>
            string.Equals(link.Rel, "next", StringComparison.OrdinalIgnoreCase));
        ProbeEnvelope<StacItemCollectionDocument>? nextPage = null;
        if (!string.IsNullOrWhiteSpace(nextLink?.Href))
        {
            nextPage = await ProbeJsonAsync(
                sourceBaseUrl,
                nextLink.Href!,
                $"Collection page follow-up {collectionId}",
                StacOpsJsonContext.Default.StacItemCollectionDocument,
                ledger,
                cancellationToken).ConfigureAwait(false);
        }

        var sampleItems = searchDocument?.Features ?? itemsDocument?.Features ?? [];
        var observedPrefixes = CollectObservedPrefixes(sampleItems);
        var observedProperties = CollectObservedProperties(sampleItems);
        var declaredExtensions = detailDocument.StacExtensions ?? [];
        var declaredKeys = declaredExtensions
            .Select(GetExtensionKeyFromUri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var observedKeys = observedPrefixes
            .Select(GetExtensionKeyFromPrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var advertisedLatestDatetime = GetAdvertisedLatestDatetime(collection);
        var latestObservedDatetime = GetLatestObservedDatetime(sampleItems);
        var cachedTemporalDriftDetected = HasCachedTemporalExtentDrift(advertisedLatestDatetime, latestObservedDatetime);
        var driftNotes = BuildCollectionDriftNotes(
            declaredKeys,
            observedKeys,
            sampleItems,
            itemsDocument,
            nextPage?.Document,
            advertisedLatestDatetime,
            latestObservedDatetime,
            cachedTemporalDriftDetected);

        var samplePreviews = sampleItems
            .Select(item => new CollectionSampleItem(
                Id: item.Id ?? "unknown",
                Datetime: GetDatetime(item),
                Summary: BuildItemSummary(item),
                ObservedPrefixes: GetObservedPrefixes(item)))
            .ToArray();

        var numberMatched = searchDocument?.NumberMatched ?? itemsDocument?.NumberMatched;
        var missingDatetimeCount = sampleItems.Count(static item => string.IsNullOrWhiteSpace(GetDatetime(item)));
        var level = DetermineCollectionLevel(detail, items, search, driftNotes, sampleItems.Length);
        var verdict = level switch
        {
            SignalLevel.Fail => "Collection probe failed",
            SignalLevel.Warn => "Compatibility warning",
            _ => "Healthy"
        };
        var detailText = BuildCollectionDetail(
            sampleItems.Length,
            numberMatched,
            missingDatetimeCount,
            driftNotes);

        return new CollectionInsight(
            Id: collectionId,
            Title: detailDocument.Title ?? detailDocument.Id ?? collectionId,
            Level: level,
            Verdict: verdict,
            Detail: detailText,
            NumberMatched: numberMatched,
            SampleCount: sampleItems.Length,
            HasItemsLink: detailDocument.Links?.Any(static link =>
                string.Equals(link.Rel, "items", StringComparison.OrdinalIgnoreCase)) == true,
            SupportsPaging: nextPage?.Document != null ||
                (itemsDocument?.NumberMatched ?? itemsDocument?.NumberReturned ?? 0) > (itemsDocument?.NumberReturned ?? 0),
            MissingDatetimeCount: missingDatetimeCount,
            LatestObservedDatetime: latestObservedDatetime,
            AdvertisedLatestDatetime: advertisedLatestDatetime,
            License: detailDocument.License,
            Keywords: detailDocument.Keywords ?? [],
            DeclaredExtensions: declaredExtensions,
            DeclaredExtensionKeys: declaredKeys,
            ObservedPrefixes: observedPrefixes,
            ObservedExtensionKeys: observedKeys,
            CachedTemporalDriftDetected: cachedTemporalDriftDetected,
            DriftNotes: driftNotes,
            ETag: detail.Request.ETag,
            EoMetric: BuildNumericMetric(sampleItems, "eo:cloud_cover", "avg cloud"),
            ProjectionMetric: BuildDistinctMetric(sampleItems, "proj:epsg", "EPSG"),
            ViewMetric: BuildNumericMetric(sampleItems, "view:sun_azimuth", "sun azimuth"),
            SampleItems: samplePreviews,
            ObservedProperties: observedProperties);
    }

    private async Task<QueryProbeInsight[]> BuildQueryProbesAsync(
        string sourceBaseUrl,
        CollectionInsight[] collections,
        bool staleMetadataSuspected,
        string[] missingCollectionIds,
        string[] staleCollectionIds,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var probes = new List<QueryProbeInsight>();
        var primaryCollection = collections
            .OrderByDescending(static collection => collection.NumberMatched ?? collection.SampleCount)
            .ThenBy(static collection => collection.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (primaryCollection is null)
        {
            probes.Add(new QueryProbeInsight(
                Label: "Search baseline",
                Level: SignalLevel.Fail,
                Verdict: "No collections discovered",
                Detail: "The demo could not find a collection to exercise live STAC search.",
                RequestPath: "/stac/search"));

            return probes.ToArray();
        }

        var escapedCollectionId = Uri.EscapeDataString(primaryCollection.Id);
        var baselineSearch = await ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/search?collections={escapedCollectionId}&limit={CollectionItemsLimit.ToString(CultureInfo.InvariantCulture)}",
            "Query workbench baseline search",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken).ConfigureAwait(false);
        probes.Add(new QueryProbeInsight(
            Label: "Baseline search",
            Level: baselineSearch.IsSuccess ? SignalLevel.Pass : SignalLevel.Fail,
            Verdict: baselineSearch.IsSuccess ? "Search returned a live sample" : "Search request failed",
            Detail: baselineSearch.IsSuccess
                ? $"{baselineSearch.Document?.NumberReturned ?? baselineSearch.Document?.Features?.Length ?? 0} item(s) returned for collection {primaryCollection.Id}."
                : "The baseline STAC search request did not return a 200 response.",
            RequestPath: baselineSearch.Request.Url));

        var pagingProbe = await BuildPagingProbeAsync(
            sourceBaseUrl,
            primaryCollection,
            ledger,
            cancellationToken).ConfigureAwait(false);
        probes.Add(pagingProbe);

        probes.Add(await BuildSortProbeAsync(sourceBaseUrl, primaryCollection, ledger, cancellationToken).ConfigureAwait(false));
        probes.Add(await BuildFieldsProbeAsync(sourceBaseUrl, primaryCollection, ledger, cancellationToken).ConfigureAwait(false));
        probes.Add(await BuildFilterProbeAsync(sourceBaseUrl, primaryCollection, ledger, cancellationToken).ConfigureAwait(false));

        probes.Add(new QueryProbeInsight(
            Label: "Metadata/search drift",
            Level: staleMetadataSuspected ? SignalLevel.Warn : SignalLevel.Pass,
            Verdict: staleMetadataSuspected ? "Freshness drift detected" : "Search scope matches metadata listings",
            Detail: staleMetadataSuspected
                ? BuildStaleMetadataDetail(missingCollectionIds, staleCollectionIds)
                : "The global search sample stayed within the collections advertised by the catalog and collection listing.",
            RequestPath: "/stac/search?limit=50"));

        return probes.ToArray();
    }

    private async Task<QueryProbeInsight> BuildPagingProbeAsync(
        string sourceBaseUrl,
        CollectionInsight collection,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var escapedCollectionId = Uri.EscapeDataString(collection.Id);
        var firstPage = await ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/search?collections={escapedCollectionId}&limit={CollectionItemsLimit.ToString(CultureInfo.InvariantCulture)}",
            "Query workbench paging page 1",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken).ConfigureAwait(false);

        if (!firstPage.IsSuccess)
        {
            return new QueryProbeInsight(
                Label: "Paging continuity",
                Level: SignalLevel.Fail,
                Verdict: "Initial page request failed",
                Detail: "The workbench could not retrieve the first search page to validate pagination.",
                RequestPath: firstPage.Request.Url);
        }

        var nextLink = firstPage.Document?.Links?.FirstOrDefault(static link =>
            string.Equals(link.Rel, "next", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(nextLink?.Href))
        {
            return new QueryProbeInsight(
                Label: "Paging continuity",
                Level: SignalLevel.Warn,
                Verdict: "Single-page sample",
                Detail: "The selected collection did not expose a next link in the sampled response, so continuity could not be proven from live data.",
                RequestPath: firstPage.Request.Url);
        }

        var secondPage = await ProbeJsonAsync(
            sourceBaseUrl,
            nextLink.Href!,
            "Query workbench paging page 2",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken).ConfigureAwait(false);

        if (!secondPage.IsSuccess)
        {
            return new QueryProbeInsight(
                Label: "Paging continuity",
                Level: SignalLevel.Fail,
                Verdict: "Follow-up page request failed",
                Detail: "The next link returned by the first page did not produce a healthy follow-up response.",
                RequestPath: secondPage.Request.Url);
        }

        var firstIds = firstPage.Document?.Features?.Select(static item => item.Id).Where(static id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var secondIds = secondPage.Document?.Features?.Select(static item => item.Id).Where(static id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overlap = firstIds.Intersect(secondIds, StringComparer.OrdinalIgnoreCase).ToArray();

        return overlap.Length == 0
            ? new QueryProbeInsight(
                Label: "Paging continuity",
                Level: SignalLevel.Pass,
                Verdict: "Pages advanced cleanly",
                Detail: "The next link returned a non-overlapping second page for the sampled collection.",
                RequestPath: secondPage.Request.Url)
            : new QueryProbeInsight(
                Label: "Paging continuity",
                Level: SignalLevel.Warn,
                Verdict: "Overlapping page contents",
                Detail: $"The sampled next link repeated item ids across pages: {string.Join(", ", overlap)}.",
                RequestPath: secondPage.Request.Url);
    }

    private async Task<QueryProbeInsight> BuildSortProbeAsync(
        string sourceBaseUrl,
        CollectionInsight collection,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var sortField = collection.ObservedProperties.FirstOrDefault(static property =>
            string.Equals(property, "observed_at", StringComparison.OrdinalIgnoreCase))
            ?? collection.ObservedProperties.FirstOrDefault(static property =>
                string.Equals(property, "quality_score", StringComparison.OrdinalIgnoreCase))
            ?? collection.ObservedProperties.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(sortField))
        {
            return new QueryProbeInsight(
                Label: "Sort probe",
                Level: SignalLevel.Warn,
                Verdict: "No sortable sample field",
                Detail: "The sampled collection did not expose a stable property to use for a live sort probe.",
                RequestPath: $"/stac/search?collections={collection.Id}&sortby=-<field>");
        }

        var escapedCollectionId = Uri.EscapeDataString(collection.Id);
        var escapedSortField = Uri.EscapeDataString($"-{sortField}");
        var probe = await ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/search?collections={escapedCollectionId}&limit=3&sortby={escapedSortField}",
            "Query workbench sort probe",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken).ConfigureAwait(false);

        if (!probe.IsSuccess)
        {
            return new QueryProbeInsight(
                Label: "Sort probe",
                Level: SignalLevel.Fail,
                Verdict: "Sort request failed",
                Detail: $"Sorting by '{sortField}' did not produce a successful response.",
                RequestPath: probe.Request.Url);
        }

        var sampledItems = probe.Document?.Features ?? [];
        if (sampledItems.Length < 2)
        {
            return new QueryProbeInsight(
                Label: "Sort probe",
                Level: SignalLevel.Warn,
                Verdict: $"Not enough samples to confirm '{sortField}'",
                Detail: "The sampled search response did not return enough items to prove descending order from live data.",
                RequestPath: probe.Request.Url);
        }

        var comparableValues = sampledItems
            .Select(item => TryReadComparable(item.Properties, sortField))
            .ToArray();

        if (comparableValues.Any(static value => !value.HasValue))
        {
            return new QueryProbeInsight(
                Label: "Sort probe",
                Level: SignalLevel.Warn,
                Verdict: $"Order not asserted for '{sortField}'",
                Detail: "The sort request succeeded, but the sampled field values were not uniformly numeric or RFC 3339 timestamps, so the dashboard did not treat their order as proof of sorting behavior.",
                RequestPath: probe.Request.Url);
        }

        var values = comparableValues
            .Select(static value => value!.Value)
            .ToArray();
        var isDescending = values.Length < 2 || values.SequenceEqual(values.OrderDescending());
        return new QueryProbeInsight(
            Label: "Sort probe",
            Level: isDescending ? SignalLevel.Pass : SignalLevel.Warn,
            Verdict: isDescending ? $"Sort honored for '{sortField}'" : $"Sort order drift for '{sortField}'",
            Detail: isDescending
                ? "The sampled search response preserved descending order across the comparable values returned."
                : "The returned sequence did not match the expected descending order for the sampled field.",
            RequestPath: probe.Request.Url);
    }

    private async Task<QueryProbeInsight> BuildFieldsProbeAsync(
        string sourceBaseUrl,
        CollectionInsight collection,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var removableField = collection.ObservedProperties.FirstOrDefault(property =>
            string.Equals(property, "name", StringComparison.OrdinalIgnoreCase))
            ?? collection.ObservedProperties.FirstOrDefault(property => !NonRemovableFieldProperties.Contains(property));

        if (string.IsNullOrWhiteSpace(removableField))
        {
            return new QueryProbeInsight(
                Label: "Fields probe",
                Level: SignalLevel.Warn,
                Verdict: "No removable property found",
                Detail: "The sampled collection did not expose a stable property that could be removed with the fields extension.",
                RequestPath: $"/stac/search?collections={collection.Id}&fields=properties,-<field>");
        }

        var escapedCollectionId = Uri.EscapeDataString(collection.Id);
        var fieldsValue = Uri.EscapeDataString($"properties,-{removableField}");
        var probe = await ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/search?collections={escapedCollectionId}&limit=1&fields={fieldsValue}",
            "Query workbench fields probe",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken).ConfigureAwait(false);

        if (!probe.IsSuccess)
        {
            return new QueryProbeInsight(
                Label: "Fields probe",
                Level: SignalLevel.Fail,
                Verdict: "Fields request failed",
                Detail: $"The fields extension request that excluded '{removableField}' did not complete successfully.",
                RequestPath: probe.Request.Url);
        }

        var feature = probe.Document?.Features?.FirstOrDefault();
        var removed = feature is not null && !feature.Properties.TryGetProperty(removableField, out _);

        return new QueryProbeInsight(
            Label: "Fields probe",
            Level: removed ? SignalLevel.Pass : SignalLevel.Warn,
            Verdict: removed ? $"Property '{removableField}' excluded" : $"Property '{removableField}' still present",
            Detail: removed
                ? "The sampled item omitted the requested property while preserving the rest of the STAC envelope."
                : "The sampled response still contained the property that the fields extension requested to remove.",
            RequestPath: probe.Request.Url);
    }

    private async Task<QueryProbeInsight> BuildFilterProbeAsync(
        string sourceBaseUrl,
        CollectionInsight collection,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        if (!collection.ObservedProperties.Contains("quality_score", StringComparer.OrdinalIgnoreCase))
        {
            return new QueryProbeInsight(
                Label: "Filter probe",
                Level: SignalLevel.Warn,
                Verdict: "No deterministic filter field",
                Detail: "The sampled collection did not expose the seeded quality_score field used for the live CQL2 filter probe.",
                RequestPath: $"/stac/search?collections={collection.Id}&filter=quality_score >= 70");
        }

        var escapedCollectionId = Uri.EscapeDataString(collection.Id);
        var filterValue = Uri.EscapeDataString("quality_score >= 70");
        var probe = await ProbeJsonAsync(
            sourceBaseUrl,
            $"/stac/search?collections={escapedCollectionId}&limit=5&filter={filterValue}&filter-lang=cql2-text",
            "Query workbench filter probe",
            StacOpsJsonContext.Default.StacItemCollectionDocument,
            ledger,
            cancellationToken).ConfigureAwait(false);

        if (!probe.IsSuccess)
        {
            return new QueryProbeInsight(
                Label: "Filter probe",
                Level: SignalLevel.Fail,
                Verdict: "Filter request failed",
                Detail: "The sampled CQL2 text filter request did not return a successful response.",
                RequestPath: probe.Request.Url);
        }

        var mismatches = probe.Document?.Features?
            .Where(item => TryReadNumeric(item.Properties, "quality_score") is double score && score < 70d)
            .Select(static item => item.Id ?? "unknown")
            .ToArray() ?? [];

        return new QueryProbeInsight(
            Label: "Filter probe",
            Level: mismatches.Length == 0 ? SignalLevel.Pass : SignalLevel.Warn,
            Verdict: mismatches.Length == 0 ? "CQL2 filter matched the sampled values" : "Filter/search mismatch detected",
            Detail: mismatches.Length == 0
                ? "Every returned item satisfied the sampled quality_score threshold."
                : $"Returned item ids below the expected threshold: {string.Join(", ", mismatches)}.",
            RequestPath: probe.Request.Url);
    }

    private static StatusCard[] BuildStatusCards(
        ProbeEnvelope<string> live,
        ProbeEnvelope<string> ready,
        ProbeEnvelope<StacCatalogDocument> catalog,
        ProbeEnvelope<StacCollectionsResponseDocument> collections,
        CollectionInsight[] collectionInsights,
        QueryProbeInsight[] queryProbes,
        HashSet<string> catalogChildIds,
        HashSet<string> collectionIds,
        bool staleMetadataSuspected,
        string[] missingCollectionIds,
        string[] staleCollectionIds)
    {
        var catalogDrift = catalogChildIds
            .Except(collectionIds, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cacheValidatorWarning = string.IsNullOrWhiteSpace(catalog.Request.ETag) ||
            string.IsNullOrWhiteSpace(collections.Request.ETag) ||
            collectionInsights.Any(static collection => string.IsNullOrWhiteSpace(collection.ETag));

        return
        [
            CreateHealthStatusCard("Liveness", live, "Healthy"),
            CreateHealthStatusCard("Readiness", ready, "Ready"),
            new StatusCard(
                Title: "Catalog",
                Level: catalog.IsSuccess && collections.IsSuccess
                    ? catalogDrift.Length == 0 ? SignalLevel.Pass : SignalLevel.Warn
                    : SignalLevel.Fail,
                Summary: catalog.IsSuccess && collections.IsSuccess
                    ? "Catalog and collection listing resolved"
                    : "Catalog discovery failed",
                Detail: catalogDrift.Length == 0
                    ? $"{collectionInsights.Length} collection(s) are visible in the current STAC listing."
                    : $"Catalog child links missing from /stac/collections: {string.Join(", ", catalogDrift)}."),
            new StatusCard(
                Title: "Cache Validators",
                Level: cacheValidatorWarning ? SignalLevel.Warn : SignalLevel.Pass,
                Summary: cacheValidatorWarning ? "Metadata validators are incomplete" : "Metadata validators are present",
                Detail: cacheValidatorWarning
                    ? "One or more STAC metadata routes did not expose an ETag header during this run."
                    : "The sampled /stac and /stac/collections* responses all advertised strong ETags."),
            new StatusCard(
                Title: "Discovery Freshness",
                Level: staleMetadataSuspected ? SignalLevel.Warn : SignalLevel.Pass,
                Summary: staleMetadataSuspected ? "Metadata cache drift suspected" : "Search and metadata stayed aligned",
                Detail: staleMetadataSuspected
                    ? BuildStaleMetadataDetail(missingCollectionIds, staleCollectionIds)
                    : "The global search sample stayed inside the cached metadata footprint."),
            new StatusCard(
                Title: "Compatibility",
                Level: HighestLevel(collectionInsights.Select(static collection => collection.Level)
                    .Concat(queryProbes.Select(static probe => probe.Level))),
                Summary: BuildCompatibilitySummary(collectionInsights, queryProbes),
                Detail: BuildCompatibilityDetail(collectionInsights, queryProbes))
        ];
    }

    private static string[] BuildNotes(
        string sourceBaseUrl,
        bool staleMetadataSuspected,
        string[] missingCollectionIds,
        string[] staleCollectionIds,
        CollectionInsight[] collectionInsights,
        QueryProbeInsight[] queryProbes)
    {
        var notes = new List<string>
        {
            $"Source: {sourceBaseUrl}",
            "Declared STAC support is taken from conformsTo and stac_extensions. Observed extension usage is inferred from live namespaced properties and assets."
        };

        if (staleMetadataSuspected)
        {
            notes.Add($"Stale metadata risk: {BuildStaleMetadataDetail(missingCollectionIds, staleCollectionIds)}");
        }

        var warningCollections = collectionInsights
            .Where(static collection => collection.Level == SignalLevel.Warn)
            .Select(static collection => $"{collection.Id}: {collection.Verdict}")
            .ToArray();
        if (warningCollections.Length > 0)
        {
            notes.Add($"Collection warnings: {string.Join(" | ", warningCollections)}");
        }

        var warningProbes = queryProbes
            .Where(static probe => probe.Level is SignalLevel.Warn or SignalLevel.Fail)
            .Select(static probe => $"{probe.Label}: {probe.Verdict}")
            .ToArray();
        if (warningProbes.Length > 0)
        {
            notes.Add($"Workbench findings: {string.Join(" | ", warningProbes)}");
        }

        return notes.ToArray();
    }

    private static StatusCard CreateHealthStatusCard(string title, ProbeEnvelope<string> probe, string expectedValue)
    {
        var body = probe.BodyText?.Trim();
        var isExpected = probe.IsSuccess && string.Equals(body, expectedValue, StringComparison.OrdinalIgnoreCase);

        return new StatusCard(
            Title: title,
            Level: isExpected ? SignalLevel.Pass : SignalLevel.Fail,
            Summary: isExpected ? body ?? expectedValue : "Health check did not return the expected payload",
            Detail: isExpected
                ? $"{title} endpoint returned '{body}' with HTTP {probe.Request.StatusCode.ToString(CultureInfo.InvariantCulture)}."
                : $"{title} endpoint returned HTTP {probe.Request.StatusCode.ToString(CultureInfo.InvariantCulture)} and body '{body ?? "<empty>"}'.");
    }

    private static string BuildCompatibilitySummary(
        CollectionInsight[] collections,
        QueryProbeInsight[] probes)
    {
        var warnings = collections.Count(static collection => collection.Level == SignalLevel.Warn) +
            probes.Count(static probe => probe.Level == SignalLevel.Warn);
        var failures = collections.Count(static collection => collection.Level == SignalLevel.Fail) +
            probes.Count(static probe => probe.Level == SignalLevel.Fail);

        return failures > 0
            ? $"{failures.ToString(CultureInfo.InvariantCulture)} blocking finding(s), {warnings.ToString(CultureInfo.InvariantCulture)} warning(s)"
            : warnings > 0
                ? $"{warnings.ToString(CultureInfo.InvariantCulture)} warning signal(s) surfaced"
                : "All sampled compatibility checks passed";
    }

    private static string BuildCompatibilityDetail(
        CollectionInsight[] collections,
        QueryProbeInsight[] probes)
    {
        var findings = collections
            .Where(static collection => collection.Level is SignalLevel.Warn or SignalLevel.Fail)
            .Select(static collection => $"{collection.Title}: {collection.Detail}")
            .Concat(probes
                .Where(static probe => probe.Level is SignalLevel.Warn or SignalLevel.Fail)
                .Select(static probe => $"{probe.Label}: {probe.Detail}"))
            .Take(3)
            .ToArray();

        return findings.Length == 0
            ? "The sampled metadata, collection probes, and query workbench did not surface compatibility drift."
            : string.Join(" ", findings);
    }

    private static string BuildStaleMetadataDetail(
        string[] missingCollectionIds,
        string[] staleCollectionIds)
    {
        var details = new List<string>();
        if (missingCollectionIds.Length > 0)
        {
            details.Add($"Search surfaced collection ids missing from cached metadata: {string.Join(", ", missingCollectionIds)}.");
        }

        if (staleCollectionIds.Length > 0)
        {
            details.Add($"Live item timestamps exceeded cached temporal extents for collection ids: {string.Join(", ", staleCollectionIds)}.");
        }

        return details.Count == 0
            ? "The global search sample stayed inside the cached metadata footprint."
            : string.Join(" ", details);
    }

    private static ExtensionInsight[] BuildExtensionInsights(IEnumerable<CollectionInsight> collections)
    {
        var rows = new List<ExtensionInsight>();
        var allKeys = collections
            .SelectMany(static collection => collection.DeclaredExtensionKeys.Concat(collection.ObservedExtensionKeys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => GetExtensionLabel(key), StringComparer.OrdinalIgnoreCase);

        foreach (var key in allKeys)
        {
            var relatedCollections = collections
                .Where(collection => collection.DeclaredExtensionKeys.Contains(key, StringComparer.OrdinalIgnoreCase) ||
                    collection.ObservedExtensionKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var declaredCollections = relatedCollections
                .Where(collection => collection.DeclaredExtensionKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                .Select(static collection => collection.Title)
                .ToArray();
            var observedCollections = relatedCollections
                .Where(collection => collection.ObservedExtensionKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                .Select(static collection => collection.Title)
                .ToArray();
            var driftCollections = relatedCollections
                .Where(collection => collection.DriftNotes.Any(note =>
                    note.Contains(GetExtensionLabel(key), StringComparison.OrdinalIgnoreCase) ||
                    note.Contains(key, StringComparison.OrdinalIgnoreCase)))
                .Select(static collection => collection.Title)
                .ToArray();

            var metricSummary = string.Join(
                " | ",
                relatedCollections
                    .Select(collection => BuildExtensionMetricSummary(collection, key))
                    .Where(static summary => !string.IsNullOrWhiteSpace(summary)));

            var level = driftCollections.Length > 0
                ? SignalLevel.Warn
                : declaredCollections.Length > 0 || observedCollections.Length > 0
                    ? SignalLevel.Pass
                    : SignalLevel.Info;

            rows.Add(new ExtensionInsight(
                Key: key,
                Label: GetExtensionLabel(key),
                Level: level,
                DeclaredSummary: declaredCollections.Length == 0
                    ? "Not declared"
                    : $"Declared by {declaredCollections.Length.ToString(CultureInfo.InvariantCulture)} collection(s)",
                ObservedSummary: observedCollections.Length == 0
                    ? "No observed usage"
                    : $"Observed in {observedCollections.Length.ToString(CultureInfo.InvariantCulture)} collection(s)",
                Verdict: driftCollections.Length == 0 ? "Aligned" : "Declaration drift",
                Detail: driftCollections.Length == 0
                    ? "Declared and observed extension signals stayed aligned in the sampled data."
                    : $"Observed usage drifted in: {string.Join(", ", driftCollections)}.",
                MetricSummary: string.IsNullOrWhiteSpace(metricSummary)
                    ? "No extension-specific metric observed in the sampled items."
                    : metricSummary));
        }

        return rows.ToArray();
    }

    private static string BuildExtensionMetricSummary(CollectionInsight collection, string key)
    {
        return key switch
        {
            "eo" when !string.IsNullOrWhiteSpace(collection.EoMetric) => $"{collection.Title}: {collection.EoMetric}",
            "proj" when !string.IsNullOrWhiteSpace(collection.ProjectionMetric) => $"{collection.Title}: {collection.ProjectionMetric}",
            "view" when !string.IsNullOrWhiteSpace(collection.ViewMetric) => $"{collection.Title}: {collection.ViewMetric}",
            _ => string.Empty
        };
    }

    private static SignalLevel DetermineCollectionLevel(
        ProbeEnvelope<StacCollectionDocument> detail,
        ProbeEnvelope<StacItemCollectionDocument> items,
        ProbeEnvelope<StacItemCollectionDocument> search,
        string[] driftNotes,
        int sampleCount)
    {
        if (!detail.IsSuccess || !items.IsSuccess || !search.IsSuccess)
        {
            return SignalLevel.Fail;
        }

        if (sampleCount == 0 || driftNotes.Length > 0)
        {
            return SignalLevel.Warn;
        }

        return SignalLevel.Pass;
    }

    private static string BuildCollectionDetail(
        int sampleCount,
        long? numberMatched,
        int missingDatetimeCount,
        string[] driftNotes)
    {
        var parts = new List<string>
        {
            numberMatched.HasValue
                ? $"Matched {numberMatched.Value.ToString(CultureInfo.InvariantCulture)} item(s)."
                : $"Sampled {sampleCount.ToString(CultureInfo.InvariantCulture)} item(s)."
        };

        if (missingDatetimeCount > 0)
        {
            parts.Add($"{missingDatetimeCount.ToString(CultureInfo.InvariantCulture)} sampled item(s) are missing datetime.");
        }

        if (driftNotes.Length > 0)
        {
            parts.AddRange(driftNotes);
        }

        return string.Join(" ", parts);
    }

    private static string[] BuildCollectionDriftNotes(
        string[] declaredKeys,
        string[] observedKeys,
        IReadOnlyList<StacItemDocument> sampleItems,
        StacItemCollectionDocument? itemsPage,
        StacItemCollectionDocument? nextPage,
        string? advertisedLatestDatetime,
        string? latestObservedDatetime,
        bool cachedTemporalDriftDetected)
    {
        var notes = new List<string>();
        var undeclaredObserved = observedKeys
            .Except(declaredKeys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => GetExtensionLabel(key), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (undeclaredObserved.Length > 0)
        {
            notes.Add($"Observed {string.Join(", ", undeclaredObserved.Select(GetExtensionLabel))} signals without matching declarations.");
        }

        var declaredWithoutEvidence = declaredKeys
            .Except(observedKeys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => GetExtensionLabel(key), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (declaredWithoutEvidence.Length > 0)
        {
            notes.Add($"Declared {string.Join(", ", declaredWithoutEvidence.Select(GetExtensionLabel))} without sampled item evidence.");
        }

        var missingDatetime = sampleItems.Count(static item => string.IsNullOrWhiteSpace(GetDatetime(item)));
        if (missingDatetime > 0)
        {
            notes.Add("Sampled items omitted the required datetime field.");
        }

        if (nextPage == null &&
            (itemsPage?.NumberMatched ?? itemsPage?.NumberReturned ?? 0) > (itemsPage?.NumberReturned ?? 0))
        {
            notes.Add("The items listing advertised more results than were returned, but the next page did not resolve.");
        }

        if (cachedTemporalDriftDetected)
        {
            notes.Add(
                $"Live item timestamps reached {latestObservedDatetime}, but the cached collection temporal extent still ended at {advertisedLatestDatetime}.");
        }

        return notes.ToArray();
    }

    private static string BuildItemSummary(StacItemDocument item)
    {
        if (item.Properties.ValueKind == JsonValueKind.Object &&
            item.Properties.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            return name.GetString() ?? item.Id ?? "item";
        }

        return item.Collection is null
            ? item.Id ?? "item"
            : $"{item.Collection}:{item.Id}";
    }

    private static string? GetLatestObservedDatetime(IEnumerable<StacItemDocument> items)
    {
        string? latestRawValue = null;
        DateTimeOffset latestInstant = default;
        var foundLatestInstant = false;

        foreach (var value in items.Select(static item => GetObservedTimestamp(item)))
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var candidateInstant))
            {
                continue;
            }

            if (!foundLatestInstant || candidateInstant > latestInstant)
            {
                latestInstant = candidateInstant;
                latestRawValue = value;
                foundLatestInstant = true;
            }
        }

        return latestRawValue;
    }

    private static string? GetAdvertisedLatestDatetime(StacCollectionDocument collection)
    {
        var intervals = collection.Extent?.Temporal?.Interval;
        if (intervals is null || intervals.Length == 0)
        {
            return null;
        }

        var primaryInterval = intervals[0];
        if (primaryInterval is null || primaryInterval.Length == 0)
        {
            return null;
        }

        return primaryInterval.Length > 1
            ? primaryInterval[1] ?? primaryInterval[0]
            : primaryInterval[0];
    }

    private static bool HasCachedTemporalExtentDrift(string? advertisedLatestDatetime, string? latestObservedDatetime)
    {
        if (string.IsNullOrWhiteSpace(advertisedLatestDatetime) || string.IsNullOrWhiteSpace(latestObservedDatetime))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
                advertisedLatestDatetime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var advertised) &&
            DateTimeOffset.TryParse(
                latestObservedDatetime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var observed) &&
            observed > advertised;
    }

    private static string[] CollectObservedPrefixes(IEnumerable<StacItemDocument> items)
    {
        return items
            .SelectMany(GetObservedPrefixes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static prefix => prefix, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] CollectObservedProperties(IEnumerable<StacItemDocument> items)
    {
        var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Properties.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in item.Properties.EnumerateObject())
            {
                properties.Add(property.Name);
            }
        }

        return properties.OrderBy(static property => property, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] GetObservedPrefixes(StacItemDocument item)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item.Properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.Properties.EnumerateObject())
            {
                AddPrefix(property.Name, prefixes);
            }
        }

        if (item.Assets.ValueKind == JsonValueKind.Object)
        {
            foreach (var asset in item.Assets.EnumerateObject())
            {
                AddPrefix(asset.Name, prefixes);
                if (asset.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var nestedProperty in asset.Value.EnumerateObject())
                {
                    AddPrefix(nestedProperty.Name, prefixes);
                }
            }
        }

        return prefixes.OrderBy(static prefix => prefix, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddPrefix(string propertyName, HashSet<string> prefixes)
    {
        var separatorIndex = propertyName.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return;
        }

        prefixes.Add(propertyName[..separatorIndex]);
    }

    private static string? BuildNumericMetric(IEnumerable<StacItemDocument> items, string propertyName, string label)
    {
        var values = items
            .Select(item => TryReadNumeric(item.Properties, propertyName))
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();

        if (values.Length == 0)
        {
            return null;
        }

        var average = values.Average();
        return $"{label} {average.ToString("0.0", CultureInfo.InvariantCulture)}";
    }

    private static string? BuildDistinctMetric(IEnumerable<StacItemDocument> items, string propertyName, string label)
    {
        var values = items
            .Select(item => TryReadString(item.Properties, propertyName))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (values.Length == 0)
        {
            return null;
        }

        return $"{label} {string.Join(", ", values)}";
    }

    private static string? GetDatetime(StacItemDocument item)
    {
        return TryReadString(item.Properties, "datetime");
    }

    private static string? GetObservedTimestamp(StacItemDocument item)
    {
        return GetDatetime(item) ?? TryReadString(item.Properties, "observed_at");
    }

    private static string? TryReadString(JsonElement properties, string propertyName)
    {
        if (properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static double? TryReadNumeric(JsonElement properties, string propertyName)
    {
        if (properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
        {
            return numeric;
        }

        return null;
    }

    private static double? TryReadComparable(JsonElement properties, string propertyName)
    {
        if (TryReadNumeric(properties, propertyName) is { } numeric)
        {
            return numeric;
        }

        var value = TryReadString(properties, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var datetime))
        {
            return datetime.ToUnixTimeMilliseconds();
        }

        return null;
    }

    private static HashSet<string> ExtractCatalogChildIds(StacCatalogDocument? catalog)
    {
        return catalog?.Links?
            .Where(static link => string.Equals(link.Rel, "child", StringComparison.OrdinalIgnoreCase))
            .Select(static link => ExtractCollectionIdFromHref(link.Href))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ExtractSearchCollectionIds(StacItemCollectionDocument? document)
    {
        return document?.Features?
            .Select(static feature => feature.Collection)
            .Where(static collection => !string.IsNullOrWhiteSpace(collection))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static collection => collection, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    private static string? ExtractCollectionIdFromHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var trimmed = href.TrimEnd('/');
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.LastOrDefault();
    }

    private async Task<ProbeEnvelope<T>> ProbeJsonAsync<T>(
        string sourceBaseUrl,
        string pathOrUrl,
        string note,
        JsonTypeInfo<T> typeInfo,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(sourceBaseUrl, pathOrUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var (response, durationMs) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            var bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var ledgerEntry = CreateLedgerEntry(request.Method.Method, requestUri, response, durationMs, note);
            ledger.Enqueue(ledgerEntry);

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeEnvelope<T>(
                    Document: default,
                    Request: ledgerEntry,
                    BodyText: ReadBodyText(bodyBytes),
                    IsSuccess: false);
            }

            var document = JsonSerializer.Deserialize(bodyBytes, typeInfo);
            return new ProbeEnvelope<T>(
                Document: document,
                Request: ledgerEntry,
                BodyText: null,
                IsSuccess: true);
        }
    }

    private async Task<ProbeEnvelope<string>> ProbeTextAsync(
        string sourceBaseUrl,
        string pathOrUrl,
        string note,
        ConcurrentQueue<RequestLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(sourceBaseUrl, pathOrUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var (response, durationMs) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            var bodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var ledgerEntry = CreateLedgerEntry(request.Method.Method, requestUri, response, durationMs, note);
            ledger.Enqueue(ledgerEntry);

            return new ProbeEnvelope<string>(
                Document: bodyText,
                Request: ledgerEntry,
                BodyText: bodyText,
                IsSuccess: response.IsSuccessStatusCode);
        }
    }

    private async Task<(HttpResponseMessage Response, long DurationMs)> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return (response, stopwatch.ElapsedMilliseconds);
    }

    private static RequestLedgerEntry CreateLedgerEntry(
        string method,
        Uri requestUri,
        HttpResponseMessage response,
        long durationMs,
        string note)
    {
        return new RequestLedgerEntry(
            Method: method,
            Url: requestUri.ToString(),
            StatusCode: (int)response.StatusCode,
            DurationMs: durationMs,
            ETag: response.Headers.ETag?.ToString(),
            CacheControl: response.Headers.CacheControl?.ToString(),
            Age: response.Headers.TryGetValues("Age", out var ages) ? ages.FirstOrDefault() : null,
            Note: note);
    }

    private static string ReadBodyText(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return System.Text.Encoding.UTF8.GetString(payload);
        }
        catch
        {
            return "<binary>";
        }
    }

    private static Uri BuildRequestUri(string sourceBaseUrl, string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        return new Uri(new Uri(EnsureTrailingSlash(sourceBaseUrl)), pathOrUrl.TrimStart('/'));
    }

    private string ResolveSourceBaseUrl(string? sourceInput)
    {
        var origin = new Uri(_httpClient.BaseAddress ?? new Uri("http://localhost/"), "/");
        if (string.IsNullOrWhiteSpace(sourceInput))
        {
            return origin.ToString().TrimEnd('/');
        }

        if (Uri.TryCreate(sourceInput, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString().TrimEnd('/');
        }

        return new Uri(origin, sourceInput).ToString().TrimEnd('/');
    }

    private static SignalLevel HighestLevel(IEnumerable<SignalLevel> levels)
    {
        var result = SignalLevel.Info;
        foreach (var level in levels)
        {
            if (level == SignalLevel.Fail)
            {
                return SignalLevel.Fail;
            }

            if (level == SignalLevel.Warn)
            {
                result = SignalLevel.Warn;
            }
            else if (level == SignalLevel.Pass && result == SignalLevel.Info)
            {
                result = SignalLevel.Pass;
            }
        }

        return result;
    }

    private static string GetExtensionKeyFromUri(string extensionUri)
    {
        foreach (var descriptor in KnownExtensions)
        {
            if (extensionUri.Contains(descriptor.UriMarker, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor.Key;
            }
        }

        return extensionUri;
    }

    private static string GetExtensionKeyFromPrefix(string prefix)
        => KnownExtensionsByKey.ContainsKey(prefix) ? prefix : prefix;

    private static string GetExtensionLabel(string key)
        => KnownExtensionsByKey.TryGetValue(key, out var descriptor)
            ? descriptor.Label
            : key;

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : $"{value}/";

    private sealed record ExtensionDescriptor(string Key, string Label, string UriMarker);
}
