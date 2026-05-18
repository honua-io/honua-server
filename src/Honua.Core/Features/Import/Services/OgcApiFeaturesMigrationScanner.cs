// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// HTTP-backed scanner for OGC API Features migration inventories.
/// </summary>
public sealed partial class OgcApiFeaturesMigrationScanner : IOgcApiFeaturesMigrationScanner
{
    private const string SourceKind = "ogc-api-features";
    private const string DisallowedNetworkAddressMessage = "OGC API Features URL resolves to a disallowed network address.";
    private const int MaxJsonDocumentRedirects = 5;

    private static readonly AsyncLocal<bool> UnsafeLocalUrlsAllowed = new();

    private readonly HttpClient _httpClient;
    private readonly ILogger<OgcApiFeaturesMigrationScanner> _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostAddressResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="OgcApiFeaturesMigrationScanner"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client used for OGC API Features discovery.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="hostAddressResolver">Optional host resolver used by tests.</param>
    public OgcApiFeaturesMigrationScanner(
        HttpClient httpClient,
        ILogger<OgcApiFeaturesMigrationScanner> logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostAddressResolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    /// <summary>
    /// Creates an HTTP handler that pins DNS resolution before connecting to an OGC API Features source host.
    /// </summary>
    /// <param name="hostAddressResolver">Optional host resolver used by tests.</param>
    /// <returns>HTTP message handler with bounded connections and pinned DNS validation.</returns>
    public static HttpMessageHandler CreatePinnedDnsHttpMessageHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        var resolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 16,
            ConnectCallback = (context, cancellationToken) =>
                ConnectWithPinnedDnsAsync(context, resolver, UnsafeLocalUrlsAllowed.Value, cancellationToken)
        };
    }

    /// <inheritdoc />
    public async Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        OgcApiFeaturesScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var serviceUri = ValidateServiceUri(request.ServiceUrl, request.AllowUnsafeLocalUrls);

        try
        {
            _ = await ResolveAllowedAddressesAsync(
                    serviceUri.DnsSafeHost,
                    _hostAddressResolver,
                    request.AllowUnsafeLocalUrls,
                    cancellationToken)
                .ConfigureAwait(false);

            var previousUnsafeLocalUrlsAllowed = UnsafeLocalUrlsAllowed.Value;
            UnsafeLocalUrlsAllowed.Value = request.AllowUnsafeLocalUrls;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds <= 0 ? 60 : request.TimeoutSeconds));

                var snapshot = await BuildSnapshotAsync(serviceUri, request, timeout.Token).ConfigureAwait(false);
                var artifact = OgcApiFeaturesMigrationInventoryScanner.BuildInventory(snapshot);
                return artifact with
                {
                    AuthPosture = new MigrationInventoryAuthPosture
                    {
                        Mode = "anonymous",
                        CredentialsSupplied = false,
                        AccessConfirmed = true
                    }
                };
            }
            finally
            {
                UnsafeLocalUrlsAllowed.Value = previousUnsafeLocalUrlsAllowed;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException ||
                                   ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                Log.ScanFailed(_logger, ToSafeSourceUrl(serviceUri), ex);
            }

            return CreateFailedArtifact(serviceUri, ToSafeScanFailureReason(ex));
        }
    }

    private async Task<OgcApiFeaturesMigrationSourceSnapshot> BuildSnapshotAsync(
        Uri serviceUri,
        OgcApiFeaturesScanRequest request,
        CancellationToken cancellationToken)
    {
        using var landingDocument = await GetJsonDocumentAsync(serviceUri, cancellationToken).ConfigureAwait(false);
        var landing = landingDocument.RootElement;
        var landingLinks = ReadLinks(landing).ToArray();
        var baseUrl = ToSafeSourceUrl(serviceUri);
        var collectionsUri = FindLinkUri(landingLinks, serviceUri, static link => IsRel(link, "data") || LinkLooksLikeCollections(link))
            ?? AppendPath(serviceUri, "collections");
        var conformanceUri = FindLinkUri(landingLinks, serviceUri, static link => IsRel(link, "conformance"))
            ?? AppendPath(serviceUri, "conformance");
        var sourceCrs = ReadCrsDeclarations(landing, "source").ToArray();

        var conformanceClasses = await TryReadConformanceClassesAsync(conformanceUri, cancellationToken).ConfigureAwait(false);
        var collections = await ReadCollectionsAsync(
                collectionsUri,
                serviceUri,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        return new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = baseUrl,
            Title = ReadString(landing, "title"),
            Description = ReadString(landing, "description"),
            Version = ReadString(landing, "version") ?? ReadString(landing, "apiVersion"),
            LandingPageLinks = landingLinks,
            ConformanceClasses = conformanceClasses,
            Collections = collections,
            CrsDeclarations = sourceCrs,
            VendorExtensions = ReadVendorExtensions(landing)
        };
    }

    private async Task<string[]> TryReadConformanceClassesAsync(Uri conformanceUri, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonDocumentAsync(conformanceUri, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("conformsTo", out var conformsTo) ||
                conformsTo.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return conformsTo.EnumerateArray()
                .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            var conformanceUrl = ToSafeSourceUrl(conformanceUri);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.OptionalDocumentUnavailable(_logger, "conformance", conformanceUrl, ex);
            }

            return [];
        }
    }

    private async Task<OgcApiFeaturesCollectionSnapshot[]> ReadCollectionsAsync(
        Uri collectionsUri,
        Uri serviceUri,
        OgcApiFeaturesScanRequest request,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonDocumentAsync(collectionsUri, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("collections", out var collectionsElement) ||
            collectionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var maxCollections = request.MaxCollections <= 0 ? 100 : request.MaxCollections;
        var collections = new List<OgcApiFeaturesCollectionSnapshot>();

        foreach (var collectionElement in collectionsElement.EnumerateArray().Take(maxCollections))
        {
            var id = ReadString(collectionElement, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            collections.Add(await ReadCollectionAsync(
                    collectionElement,
                    id,
                    serviceUri,
                    request,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return collections
            .OrderBy(static collection => collection.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<OgcApiFeaturesCollectionSnapshot> ReadCollectionAsync(
        JsonElement collectionElement,
        string id,
        Uri serviceUri,
        OgcApiFeaturesScanRequest request,
        CancellationToken cancellationToken)
    {
        var links = ReadLinks(collectionElement).ToArray();
        var itemEncodings = links
            .Where(static link => IsRel(link, "items") || LinkLooksLikeItems(link))
            .Select(static link => link.Type)
            .Where(static type => !string.IsNullOrWhiteSpace(type))
            .Select(static type => type!)
            .ToArray();
        var fields = await ReadCollectionFieldsAsync(links, serviceUri, cancellationToken).ConfigureAwait(false);
        var itemsProbe = await ProbeItemsAsync(id, links, serviceUri, request, cancellationToken).ConfigureAwait(false);

        return new OgcApiFeaturesCollectionSnapshot
        {
            Id = id,
            Title = ReadString(collectionElement, "title"),
            Description = ReadString(collectionElement, "description"),
            GeometryType = itemsProbe.GeometryType,
            FeatureCount = itemsProbe.FeatureCount ?? ReadNullableInt32(collectionElement, "itemCount"),
            Links = links,
            PaginationLinks = itemsProbe.PaginationLinks,
            CrsDeclarations = ReadCrsDeclarations(collectionElement, "collection").ToArray(),
            ItemEncodings = itemEncodings.Concat(itemsProbe.ItemEncodings).ToArray(),
            Fields = fields,
            VendorExtensions = ReadVendorExtensions(collectionElement)
        };
    }

    private async Task<MigrationInventoryField[]> ReadCollectionFieldsAsync(
        OgcApiFeaturesLink[] links,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        var fieldDocuments = links
            .Where(static link => IsRel(link, "queryables") ||
                                  IsRel(link, "http://www.opengis.net/def/rel/ogc/1.0/queryables") ||
                                  IsRel(link, "describedby") ||
                                  IsRel(link, "schema") ||
                                  IsRel(link, "http://www.opengis.net/def/rel/ogc/1.0/schema"))
            .Select(link => ResolveUri(link.Href, baseUri))
            .Where(static uri => uri != null)
            .Select(static uri => uri!)
            .DistinctBy(static uri => uri.ToString(), StringComparer.Ordinal)
            .ToArray();

        var fields = new List<MigrationInventoryField>();
        foreach (var uri in fieldDocuments)
        {
            try
            {
                using var document = await GetJsonDocumentAsync(uri, cancellationToken).ConfigureAwait(false);
                fields.AddRange(ReadFields(document.RootElement));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                var documentUrl = ToSafeSourceUrl(uri);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    Log.OptionalDocumentUnavailable(_logger, "schema-queryables", documentUrl, ex);
                }
            }
        }

        return fields
            .GroupBy(static field => field.Name, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static field => field.Alias != null).First())
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<ItemsProbeResult> ProbeItemsAsync(
        string collectionId,
        OgcApiFeaturesLink[] links,
        Uri baseUri,
        OgcApiFeaturesScanRequest request,
        CancellationToken cancellationToken)
    {
        var itemsUri = FindLinkUri(links, baseUri, static link => IsRel(link, "items") || LinkLooksLikeItems(link))
            ?? AppendPath(baseUri, $"collections/{Uri.EscapeDataString(collectionId)}/items");
        itemsUri = AddMissingQueryParameter(itemsUri, "limit", Math.Max(request.ItemsProbeLimit, 1).ToString(CultureInfo.InvariantCulture));

        try
        {
            using var document = await GetJsonDocumentAsync(itemsUri, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var paginationLinks = ReadLinks(root)
                .Where(static link => IsRel(link, "next") || IsRel(link, "prev") || IsRel(link, "previous"))
                .ToArray();
            var geometryType = ReadFirstFeatureGeometryType(root);
            var featureCount = ReadNullableInt32(root, "numberMatched");
            var encodings = new[] { "application/geo+json" };

            return new ItemsProbeResult(featureCount, geometryType, paginationLinks, encodings);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            var itemsUrl = ToSafeSourceUrl(itemsUri);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                Log.OptionalDocumentUnavailable(_logger, "items", itemsUrl, ex);
            }

            return new ItemsProbeResult(null, null, [], []);
        }
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(Uri uri, CancellationToken cancellationToken)
    {
        var requestUri = uri;

        for (var redirectCount = 0;; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, requestUri);
            message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Accept.ParseAdd("application/geo+json");

            using var response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirectCount >= MaxJsonDocumentRedirects)
                {
                    throw new HttpRequestException("OGC API Features endpoint returned too many redirects.");
                }

                var redirectUri = ResolveRedirectUri(requestUri, response.Headers.Location);
                if (redirectUri == null || !IsSafeRedirectUri(requestUri, redirectUri))
                {
                    throw new HttpRequestException("OGC API Features endpoint returned an unsafe redirect.");
                }

                requestUri = redirectUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static Uri? ResolveRedirectUri(Uri requestUri, Uri? location)
    {
        if (location == null)
        {
            return null;
        }

        return location.IsAbsoluteUri
            ? location
            : Uri.TryCreate(requestUri, location, out var redirectUri)
                ? redirectUri
                : null;
    }

    private static bool IsSafeRedirectUri(Uri requestUri, Uri redirectUri)
    {
        if (redirectUri.Scheme != Uri.UriSchemeHttp &&
            redirectUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(redirectUri.UserInfo) ||
            !string.Equals(requestUri.DnsSafeHost, redirectUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requestUri.Scheme == Uri.UriSchemeHttps && redirectUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (requestUri.Scheme == redirectUri.Scheme)
        {
            return requestUri.Port == redirectUri.Port;
        }

        return requestUri.Scheme == Uri.UriSchemeHttp &&
               redirectUri.Scheme == Uri.UriSchemeHttps &&
               IsDefaultPort(requestUri) &&
               IsDefaultPort(redirectUri);
    }

    private static bool IsDefaultPort(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return uri.Port == 80;
        }

        return uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443;
    }

    private static IEnumerable<MigrationInventoryField> ReadFields(JsonElement root)
    {
        var properties = FindPropertiesObject(root);
        if (properties == null)
        {
            yield break;
        }

        foreach (var property in properties.Value.EnumerateObject()
                     .OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (string.Equals(property.Name, "geometry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new MigrationInventoryField
            {
                Name = property.Name,
                Alias = ReadString(property.Value, "title"),
                FieldType = ReadJsonSchemaType(property.Value),
                Nullable = ReadNullable(property.Value)
            };
        }
    }

    private static JsonElement? FindPropertiesObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            return properties;
        }

        if (root.TryGetProperty("schema", out var schema) &&
            schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var schemaProperties) &&
            schemaProperties.ValueKind == JsonValueKind.Object)
        {
            return schemaProperties;
        }

        return null;
    }

    private static string ReadJsonSchemaType(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return "unknown";
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() ?? "unknown";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            var types = type.EnumerateArray()
                .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(static value => !string.IsNullOrWhiteSpace(value) && value != "null")
                .Select(static value => value!)
                .ToArray();
            return types.Length == 0 ? "unknown" : string.Join("|", types);
        }

        return "unknown";
    }

    private static bool? ReadNullable(JsonElement property)
    {
        if (property.TryGetProperty("nullable", out var nullable) &&
            nullable.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return nullable.GetBoolean();
        }

        if (!property.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return type.EnumerateArray()
            .Any(static item => item.ValueKind == JsonValueKind.String &&
                                string.Equals(item.GetString(), "null", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<OgcApiFeaturesCrsDeclaration> ReadCrsDeclarations(JsonElement element, string defaultRole)
    {
        if (element.TryGetProperty("storageCrs", out var storageCrs) &&
            storageCrs.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(storageCrs.GetString()))
        {
            yield return new OgcApiFeaturesCrsDeclaration
            {
                Role = "storage",
                Value = storageCrs.GetString()!
            };
        }

        if (element.TryGetProperty("crs", out var crs) &&
            crs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in crs.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    yield return new OgcApiFeaturesCrsDeclaration
                    {
                        Role = "supported",
                        Value = item.GetString()!
                    };
                }
            }
        }

        if (element.TryGetProperty("extent", out var extent) &&
            extent.ValueKind == JsonValueKind.Object &&
            extent.TryGetProperty("spatial", out var spatial) &&
            spatial.ValueKind == JsonValueKind.Object &&
            spatial.TryGetProperty("crs", out var spatialCrs) &&
            spatialCrs.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(spatialCrs.GetString()))
        {
            yield return new OgcApiFeaturesCrsDeclaration
            {
                Role = "extent",
                Value = spatialCrs.GetString()!
            };
        }

        if (!element.TryGetProperty("crs", out _) &&
            !element.TryGetProperty("storageCrs", out _) &&
            !element.TryGetProperty("extent", out _) &&
            string.Equals(defaultRole, "source", StringComparison.Ordinal))
        {
            yield break;
        }
    }

    private static OgcApiFeaturesLink[] ReadLinks(JsonElement element)
    {
        if (!element.TryGetProperty("links", out var links) ||
            links.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return links.EnumerateArray()
            .Where(static link => link.ValueKind == JsonValueKind.Object)
            .Select(static link => new OgcApiFeaturesLink
            {
                Rel = ReadString(link, "rel") ?? "related",
                Href = ReadString(link, "href") ?? string.Empty,
                Type = ReadString(link, "type"),
                Title = ReadString(link, "title")
            })
            .Where(static link => !string.IsNullOrWhiteSpace(link.Href))
            .OrderBy(static link => link.Rel, StringComparer.Ordinal)
            .ThenBy(static link => link.Href, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ReadVendorExtensions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return element.EnumerateObject()
            .Select(static property => property.Name)
            .Where(static name => name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadFirstFeatureGeometryType(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind == JsonValueKind.Object &&
                feature.TryGetProperty("geometry", out var geometry) &&
                geometry.ValueKind == JsonValueKind.Object &&
                geometry.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String)
            {
                return type.GetString();
            }
        }

        return null;
    }

    private static Uri? FindLinkUri(
        IEnumerable<OgcApiFeaturesLink> links,
        Uri baseUri,
        Func<OgcApiFeaturesLink, bool> predicate)
        => links.Where(predicate)
            .Select(link => ResolveUri(link.Href, baseUri))
            .FirstOrDefault(static uri => uri != null);

    private static bool IsRel(OgcApiFeaturesLink link, string rel)
        => string.Equals(link.Rel, rel, StringComparison.OrdinalIgnoreCase);

    private static bool LinkLooksLikeCollections(OgcApiFeaturesLink link)
        => link.Href.Contains("/collections", StringComparison.OrdinalIgnoreCase);

    private static bool LinkLooksLikeItems(OgcApiFeaturesLink link)
        => link.Href.Contains("/items", StringComparison.OrdinalIgnoreCase);

    private static Uri? ResolveUri(string href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var absolute)
            ? absolute
            : Uri.TryCreate(EnsureDirectoryBase(baseUri), href, out var relative)
                ? relative
                : null;
    }

    private static Uri AppendPath(Uri baseUri, string relativePath)
    {
        var root = EnsureDirectoryBase(baseUri);
        return new Uri(root, relativePath.TrimStart('/'));
    }

    private static Uri EnsureDirectoryBase(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (builder.Path.Length == 0 || builder.Path[^1] != '/')
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static Uri AddMissingQueryParameter(Uri uri, string name, string value)
    {
        var parameters = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (parameters.Any(parameter =>
                parameter.Split('=', 2)[0].Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return uri;
        }

        parameters.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        var builder = new UriBuilder(uri)
        {
            Query = string.Join("&", parameters)
        };
        return builder.Uri;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadNullableInt32(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static MigrationSourceInventoryArtifact CreateFailedArtifact(Uri serviceUri, string reason)
        => new()
        {
            SourceKind = SourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = "OGC API Features",
                BaseUrl = ToSafeSourceUrl(serviceUri),
                Product = "OGC API Features",
                ServiceType = "OGC API Features"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous-or-auth-required",
                CredentialsSupplied = false,
                AccessConfirmed = false,
                Notes = [reason]
            },
            ScanCompleteness = MigrationInventoryHelpers.BuildCompleteness("failed", [reason], ["source-inventory"]),
            Summary = new MigrationInventorySummary(),
            OverallCompatibility = MigrationInventoryHelpers.Partial(
                "The OGC API Features scan did not complete successfully.",
                [reason],
                ["Verify OGC API Features landing page reachability, JSON support, and access requirements, then rerun the scan."],
                OgcApiFeaturesImportCompatibilityCodes.ManualReview)
        };

    private static string ToSafeScanFailureReason(Exception exception)
        => exception switch
        {
            TaskCanceledException => "OGC API Features scan timed out.",
            HttpRequestException => "OGC API Features endpoint could not be reached or returned an unsupported response.",
            JsonException => "OGC API Features metadata could not be parsed as JSON.",
            _ => "OGC API Features metadata could not be scanned."
        };

    private static Uri ValidateServiceUri(string serviceUrl, bool allowUnsafeLocalUrls)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            (allowUnsafeLocalUrls
                ? uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                : uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpRequestException(allowUnsafeLocalUrls
                ? "OGC API Features URL must be a valid HTTP or HTTPS URL."
                : "OGC API Features URL must be a valid HTTPS URL.");
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new HttpRequestException("OGC API Features URL must not include embedded credentials.");
        }

        if (!allowUnsafeLocalUrls &&
            (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        return uri;
    }

    private static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool allowUnsafeLocalUrls,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (!allowUnsafeLocalUrls && IsPrivateOrReservedAddress(literalAddress))
            {
                throw new HttpRequestException(DisallowedNetworkAddressMessage);
            }

            return [literalAddress];
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }
        catch (ArgumentException)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        if (!allowUnsafeLocalUrls)
        {
            foreach (var address in addresses)
            {
                if (IsPrivateOrReservedAddress(address))
                {
                    throw new HttpRequestException(DisallowedNetworkAddressMessage);
                }
            }
        }

        return addresses;
    }

    private static async ValueTask<Stream> ConnectWithPinnedDnsAsync(
        SocketsHttpConnectionContext context,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool allowUnsafeLocalUrls,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(
                context.DnsEndPoint.Host,
                hostAddressResolver,
                allowUnsafeLocalUrls,
                cancellationToken)
            .ConfigureAwait(false);

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var connected = false;

            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                connected = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastException = ex;
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        }

        throw new HttpRequestException("Unable to establish a connection to the OGC API Features host.", lastException);
    }

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) ||
                   (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                   bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6Multicast ||
                   address.IsIPv6SiteLocal ||
                   bytes.All(static value => value == 0) ||
                   bytes[0] == 0xfc ||
                   bytes[0] == 0xfd ||
                   bytes[0] == 0xfe;
        }

        return true;
    }

    private static string ToSafeSourceUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private sealed record ItemsProbeResult(
        int? FeatureCount,
        string? GeometryType,
        OgcApiFeaturesLink[] PaginationLinks,
        string[] ItemEncodings);

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 21001,
            Level = LogLevel.Warning,
            Message = "OGC API Features migration scan failed for {SourceUrl}.",
            SkipEnabledCheck = true)]
        public static partial void ScanFailed(ILogger logger, string sourceUrl, Exception exception);

        [LoggerMessage(
            EventId = 21002,
            Level = LogLevel.Debug,
            Message = "Optional OGC API Features {DocumentKind} document was unavailable at {DocumentUrl}.",
            SkipEnabledCheck = true)]
        public static partial void OptionalDocumentUnavailable(
            ILogger logger,
            string documentKind,
            string documentUrl,
            Exception exception);
    }
}
