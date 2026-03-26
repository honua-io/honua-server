// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// HTTP client for communicating with GeoServer REST API.
/// </summary>
internal sealed partial class GeoServerRestClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GeoServerRestClient> _logger;

    public GeoServerRestClient(HttpClient httpClient, ILogger<GeoServerRestClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Discover GeoServer service metadata, workspaces, datastores, layers and styles.
    /// </summary>
    public async Task<GeoServerServiceInfo> DiscoverServiceAsync(
        string geoServerRestUrl,
        string? username,
        string? password,
        bool includeCompatibilityAnalysis,
        bool includeStyleContent,
        int timeoutSeconds,
        int maxRetryAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(geoServerRestUrl);

        Log.DiscoveringService(_logger, geoServerRestUrl);

        using var activity = GeoServerImportActivity.StartDiscovery(geoServerRestUrl);

        var baseUrl = geoServerRestUrl.TrimEnd('/');

        // Setup authentication if provided
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        else
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        try
        {
            // Get server version information
            var serverInfo = await GetServerVersionAsync(baseUrl, cancellationToken);

            // Get global settings
            var globalSettings = await GetGlobalSettingsAsync(baseUrl, cancellationToken);

            // Get all workspaces
            var workspaces = await GetWorkspacesAsync(baseUrl, cancellationToken);

            // Get all datastores across workspaces
            var dataStores = await GetDataStoresAsync(baseUrl, workspaces, cancellationToken);

            // Get all coverage stores across workspaces
            var coverageStores = await GetCoverageStoresAsync(baseUrl, workspaces, cancellationToken);

            // Get all layers across workspaces
            var layers = await GetLayersAsync(baseUrl, workspaces, cancellationToken);

            // Get all layer groups across workspaces
            var layerGroups = await GetLayerGroupsAsync(baseUrl, workspaces, cancellationToken);

            // Get all styles across workspaces
            var styles = await GetStylesAsync(baseUrl, workspaces, includeStyleContent, cancellationToken);

            // Perform compatibility analysis
            var compatibility = includeCompatibilityAnalysis
                ? PerformCompatibilityAnalysis(dataStores, coverageStores, layers, styles)
                : null;

            var result = new GeoServerServiceInfo
            {
                GeoServerRestUrl = geoServerRestUrl,
                Version = serverInfo.Version,
                BuildTimestamp = serverInfo.BuildTimestamp,
                GitRevision = serverInfo.GitRevision,
                GlobalSettings = globalSettings,
                Workspaces = workspaces,
                DataStores = dataStores,
                CoverageStores = coverageStores,
                Layers = layers,
                LayerGroups = layerGroups,
                Styles = styles,
                CompatibilityAssessment = compatibility,
                DiscoveredAt = DateTimeOffset.UtcNow
            };

            Log.ServiceDiscovered(_logger, workspaces.Length, dataStores.Length, layers.Length);

            return result;
        }
        catch (HttpRequestException ex)
        {
            Log.ServiceDiscoveryHttpFailed(_logger, geoServerRestUrl, ex);
            throw new InvalidOperationException("Failed to connect to GeoServer.", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Log.ServiceDiscoveryTimedOut(_logger, geoServerRestUrl, ex);
            throw new InvalidOperationException("Timeout discovering GeoServer service.", ex);
        }
        catch (Exception ex)
        {
            Log.ServiceDiscoveryUnexpectedError(_logger, geoServerRestUrl, ex);
            throw;
        }
    }

    private async Task<(string? Version, string? BuildTimestamp, string? GitRevision)> GetServerVersionAsync(
        string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(BuildXmlUrl(baseUrl, "about/version"), cancellationToken);
            var doc = XDocument.Parse(response);

            var aboutElement = doc.Root?.Name.LocalName == "about"
                ? doc.Root
                : doc.Root?.Element("about");
            XElement? versionElement = null;
            if (aboutElement != null)
            {
                foreach (var resourceElement in aboutElement.Elements("resource"))
                {
                    if (!string.Equals(resourceElement.Attribute("name")?.Value, "GeoServer", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    versionElement = resourceElement;
                    break;
                }

                versionElement ??= aboutElement.Element("resource");
            }

            if (versionElement != null)
            {
                return (
                    Version: versionElement.Element("Version")?.Value,
                    BuildTimestamp: versionElement.Element("Build-Timestamp")?.Value,
                    GitRevision: versionElement.Element("Git-Revision")?.Value
                );
            }
        }
        catch (Exception ex)
        {
            Log.VersionUnavailable(_logger, ex);
        }

        return (null, null, null);
    }

    private async Task<GeoServerGlobalSettings?> GetGlobalSettingsAsync(
        string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetRequiredJsonDocumentAsync(BuildJsonUrl(baseUrl, "settings"), cancellationToken).ConfigureAwait(false);

            if (json.RootElement.TryGetProperty("global", out var globalElement))
            {
                return new GeoServerGlobalSettings
                {
                    Title = GetOptionalStringProperty(globalElement, "title"),
                    Abstract = GetOptionalStringProperty(globalElement, "abstract"),
                    OnlineResource = GetOptionalStringProperty(globalElement, "onlineResource"),
                    SchemaBaseUrl = GetOptionalStringProperty(globalElement, "schemaBaseURL"),
                    ProxyBaseUrl = GetOptionalStringProperty(globalElement, "proxyBaseUrl"),
                    Charset = GetOptionalStringProperty(globalElement, "charset"),
                    NumDecimals = GetOptionalIntProperty(globalElement, "numDecimals"),
                    VerboseMessages = GetOptionalBoolProperty(globalElement, "verbose"),
                    VerboseExceptions = GetOptionalBoolProperty(globalElement, "verboseExceptions")
                };
            }
        }
        catch (Exception ex)
        {
            Log.GlobalSettingsUnavailable(_logger, ex);
        }

        return null;
    }

    private async Task<GeoServerWorkspaceInfo[]> GetWorkspacesAsync(
        string baseUrl, CancellationToken cancellationToken)
    {
        using var json = await GetRequiredJsonDocumentAsync(BuildJsonUrl(baseUrl, "workspaces"), cancellationToken).ConfigureAwait(false);

        var workspaces = new List<GeoServerWorkspaceInfo>();

        foreach (var workspaceElement in EnumerateCollectionItems(json.RootElement, "workspaces", "workspace"))
        {
            var workspaceName = TryGetName(workspaceElement);
            if (!string.IsNullOrEmpty(workspaceName))
            {
                workspaces.Add(await GetWorkspaceDetailsAsync(baseUrl, workspaceName, cancellationToken).ConfigureAwait(false));
            }
        }

        return workspaces.ToArray();
    }

    private async Task<GeoServerWorkspaceInfo> GetWorkspaceDetailsAsync(
        string baseUrl, string workspaceName, CancellationToken cancellationToken)
    {
        using var json = await GetRequiredJsonDocumentAsync(
            BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspaceName)}"),
            cancellationToken).ConfigureAwait(false);

        if (json.RootElement.TryGetProperty("workspace", out var workspaceElement))
        {
            return new GeoServerWorkspaceInfo
            {
                Name = workspaceElement.GetProperty("name").GetString() ?? string.Empty,
                IsDefault = GetOptionalBoolProperty(workspaceElement, "default") ?? false,
                IsIsolated = GetOptionalBoolProperty(workspaceElement, "isolated") ?? false,
                DateCreated = GetOptionalDateTimeProperty(workspaceElement, "dateCreated"),
                DateModified = GetOptionalDateTimeProperty(workspaceElement, "dateModified")
            };
        }

        throw new InvalidOperationException($"GeoServer workspace '{workspaceName}' was returned without a workspace payload.");
    }

    private async Task<GeoServerDataStoreInfo[]> GetDataStoresAsync(
        string baseUrl, GeoServerWorkspaceInfo[] workspaces, CancellationToken cancellationToken)
    {
        var dataStores = new List<GeoServerDataStoreInfo>();

        foreach (var workspace in workspaces)
        {
            using var json = await GetRequiredJsonDocumentAsync(
                BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspace.Name)}/datastores"),
                cancellationToken).ConfigureAwait(false);

            foreach (var dataStoreElement in EnumerateCollectionItems(json.RootElement, "dataStores", "dataStore"))
            {
                var dataStoreName = TryGetName(dataStoreElement);
                if (!string.IsNullOrEmpty(dataStoreName))
                {
                    dataStores.Add(await GetDataStoreDetailsAsync(baseUrl, workspace.Name, dataStoreName, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return dataStores.ToArray();
    }

    private async Task<GeoServerDataStoreInfo> GetDataStoreDetailsAsync(
        string baseUrl, string workspaceName, string dataStoreName, CancellationToken cancellationToken)
    {
        using var json = await GetRequiredJsonDocumentAsync(
            BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspaceName)}/datastores/{EscapePathSegment(dataStoreName)}"),
            cancellationToken).ConfigureAwait(false);

        if (json.RootElement.TryGetProperty("dataStore", out var dataStoreElement))
        {
            var connectionParams = new Dictionary<string, object>();

            foreach (var entry in EnumerateCollectionItems(dataStoreElement, "connectionParameters", "entry"))
            {
                if (entry.TryGetProperty("@key", out var keyProp) &&
                    entry.TryGetProperty("$", out var valueProp))
                {
                    var key = keyProp.GetString();
                    var value = valueProp.GetString();
                    if (key != null && value != null)
                    {
                        connectionParams[key] = value;
                    }
                }
            }

            var dataStoreType = ResolveDataStoreType(dataStoreElement, connectionParams);
            var compatibility = AssessDataStoreCompatibility(dataStoreType, connectionParams);

            return new GeoServerDataStoreInfo
            {
                Name = dataStoreElement.GetProperty("name").GetString() ?? string.Empty,
                WorkspaceName = workspaceName,
                Description = GetOptionalStringProperty(dataStoreElement, "description"),
                Type = dataStoreType,
                ConnectionParameters = connectionParams,
                Enabled = GetOptionalBoolProperty(dataStoreElement, "enabled") ?? true,
                IsDefault = GetOptionalBoolProperty(dataStoreElement, "default") ?? false,
                Compatibility = compatibility
            };
        }

        throw new InvalidOperationException($"GeoServer datastore '{workspaceName}/{dataStoreName}' was returned without a dataStore payload.");
    }

    private async Task<GeoServerCoverageStoreInfo[]> GetCoverageStoresAsync(
        string baseUrl, GeoServerWorkspaceInfo[] workspaces, CancellationToken cancellationToken)
    {
        var coverageStores = new List<GeoServerCoverageStoreInfo>();

        foreach (var workspace in workspaces)
        {
            using var json = await GetRequiredJsonDocumentAsync(
                BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspace.Name)}/coveragestores"),
                cancellationToken).ConfigureAwait(false);

            foreach (var coverageStoreElement in EnumerateCollectionItems(json.RootElement, "coverageStores", "coverageStore"))
            {
                var coverageStoreName = TryGetName(coverageStoreElement);
                if (!string.IsNullOrEmpty(coverageStoreName))
                {
                    coverageStores.Add(await GetCoverageStoreDetailsAsync(baseUrl, workspace.Name, coverageStoreName, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return coverageStores.ToArray();
    }

    private async Task<GeoServerCoverageStoreInfo> GetCoverageStoreDetailsAsync(
        string baseUrl, string workspaceName, string coverageStoreName, CancellationToken cancellationToken)
    {
        using var json = await GetRequiredJsonDocumentAsync(
            BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspaceName)}/coveragestores/{EscapePathSegment(coverageStoreName)}"),
            cancellationToken).ConfigureAwait(false);

        if (json.RootElement.TryGetProperty("coverageStore", out var coverageStoreElement))
        {
            var connectionParams = new Dictionary<string, object>();

            if (coverageStoreElement.TryGetProperty("url", out var urlProp))
            {
                var url = urlProp.GetString();
                if (url != null)
                {
                    connectionParams["url"] = url;
                }
            }

            var coverageStoreType = coverageStoreElement.GetProperty("type").GetString() ?? string.Empty;
            var compatibility = AssessCoverageStoreCompatibility(coverageStoreType, connectionParams);

            return new GeoServerCoverageStoreInfo
            {
                Name = coverageStoreElement.GetProperty("name").GetString() ?? string.Empty,
                WorkspaceName = workspaceName,
                Description = GetOptionalStringProperty(coverageStoreElement, "description"),
                Type = coverageStoreType,
                ConnectionParameters = connectionParams,
                Enabled = GetOptionalBoolProperty(coverageStoreElement, "enabled") ?? true,
                IsDefault = GetOptionalBoolProperty(coverageStoreElement, "default") ?? false,
                Compatibility = compatibility
            };
        }

        throw new InvalidOperationException($"GeoServer coverage store '{workspaceName}/{coverageStoreName}' was returned without a coverageStore payload.");
    }

    private async Task<GeoServerLayerInfo[]> GetLayersAsync(
        string baseUrl, GeoServerWorkspaceInfo[] workspaces, CancellationToken cancellationToken)
    {
        var layers = new List<GeoServerLayerInfo>();

        foreach (var workspace in workspaces)
        {
            using var json = await GetRequiredJsonDocumentAsync(
                BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspace.Name)}/layers"),
                cancellationToken).ConfigureAwait(false);

            foreach (var layerElement in EnumerateCollectionItems(json.RootElement, "layers", "layer"))
            {
                var layerName = TryGetName(layerElement);
                if (!string.IsNullOrEmpty(layerName))
                {
                    layers.Add(await GetLayerDetailsAsync(baseUrl, workspace.Name, layerName, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return layers.ToArray();
    }

    private async Task<GeoServerLayerInfo> GetLayerDetailsAsync(
        string baseUrl, string workspaceName, string layerName, CancellationToken cancellationToken)
    {
        using var json = await GetRequiredJsonDocumentAsync(
            BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspaceName)}/layers/{EscapePathSegment(layerName)}"),
            cancellationToken).ConfigureAwait(false);

        if (json.RootElement.TryGetProperty("layer", out var layerElement))
        {
            var alternativeStyles = new List<string>();
            foreach (var style in EnumerateCollectionItems(layerElement, "styles", "style"))
            {
                var styleName = TryGetName(style);
                if (styleName != null)
                {
                    alternativeStyles.Add(styleName);
                }
            }

            var keywords = new List<string>();
            foreach (var keyword in EnumerateCollectionItems(layerElement, "keywords", "string"))
            {
                if (keyword.ValueKind == JsonValueKind.String)
                {
                    var keywordStr = keyword.GetString();
                    if (keywordStr != null)
                    {
                        keywords.Add(keywordStr);
                    }
                }
            }

            var metadata = new Dictionary<string, object>();
            if (layerElement.TryGetProperty("metadata", out var metadataElement) &&
                metadataElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var metadataProperty in metadataElement.EnumerateObject())
                {
                    if (metadataProperty.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = metadataProperty.Value.GetString();
                        if (value != null)
                        {
                            metadata[metadataProperty.Name] = value;
                        }
                    }
                }
            }

            var (dataStoreName, coverageStoreName) = ExtractStoreNames(layerElement);
            var compatibility = AssessLayerCompatibility(layerElement);

            return new GeoServerLayerInfo
            {
                Name = layerElement.GetProperty("name").GetString() ?? string.Empty,
                WorkspaceName = workspaceName,
                Title = GetOptionalStringProperty(layerElement, "title"),
                Abstract = GetOptionalStringProperty(layerElement, "abstract"),
                DefaultStyle = GetNamedObjectNameProperty(layerElement, "defaultStyle"),
                AlternativeStyles = alternativeStyles.ToArray(),
                DataStoreName = dataStoreName,
                CoverageStoreName = coverageStoreName,
                NativeName = GetOptionalStringProperty(layerElement, "nativeName"),
                SRS = GetOptionalStringProperty(layerElement, "srs"),
                NativeCRS = GetOptionalStringProperty(layerElement, "nativeCRS"),
                Enabled = GetOptionalBoolProperty(layerElement, "enabled") ?? true,
                Queryable = GetOptionalBoolProperty(layerElement, "queryable") ?? true,
                Opaque = GetOptionalBoolProperty(layerElement, "opaque") ?? false,
                Keywords = keywords.ToArray(),
                Metadata = metadata,
                Compatibility = compatibility
            };
        }

        throw new InvalidOperationException($"GeoServer layer '{workspaceName}/{layerName}' was returned without a layer payload.");
    }

    private async Task<GeoServerLayerGroupInfo[]> GetLayerGroupsAsync(
        string baseUrl, GeoServerWorkspaceInfo[] workspaces, CancellationToken cancellationToken)
    {
        var layerGroups = new List<GeoServerLayerGroupInfo>();

        // Get global layer groups
        using (var json = await GetRequiredJsonDocumentAsync(BuildJsonUrl(baseUrl, "layergroups"), cancellationToken).ConfigureAwait(false))
        {
            foreach (var groupElement in EnumerateCollectionItems(json.RootElement, "layerGroups", "layerGroup"))
            {
                var groupName = TryGetName(groupElement);
                if (!string.IsNullOrEmpty(groupName))
                {
                    layerGroups.Add(await GetLayerGroupDetailsAsync(baseUrl, null, groupName, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        // Get workspace-specific layer groups
        foreach (var workspace in workspaces)
        {
            using var json = await GetRequiredJsonDocumentAsync(
                BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspace.Name)}/layergroups"),
                cancellationToken).ConfigureAwait(false);

            foreach (var groupElement in EnumerateCollectionItems(json.RootElement, "layerGroups", "layerGroup"))
            {
                var groupName = TryGetName(groupElement);
                if (!string.IsNullOrEmpty(groupName))
                {
                    layerGroups.Add(await GetLayerGroupDetailsAsync(baseUrl, workspace.Name, groupName, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return layerGroups.ToArray();
    }

    private async Task<GeoServerLayerGroupInfo> GetLayerGroupDetailsAsync(
        string baseUrl, string? workspaceName, string groupName, CancellationToken cancellationToken)
    {
        var url = workspaceName == null
            ? BuildJsonUrl(baseUrl, $"layergroups/{EscapePathSegment(groupName)}")
            : BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspaceName)}/layergroups/{EscapePathSegment(groupName)}");
        using var json = await GetRequiredJsonDocumentAsync(url, cancellationToken).ConfigureAwait(false);

        if (json.RootElement.TryGetProperty("layerGroup", out var groupElement))
        {
            var layers = new List<GeoServerLayerGroupEntry>();
            foreach (var published in EnumerateCollectionItems(groupElement, "publishables", "published"))
            {
                var entryName = TryGetName(published);
                if (entryName != null)
                {
                    layers.Add(new GeoServerLayerGroupEntry
                    {
                        Name = entryName,
                        Type = GetOptionalStringProperty(published, "@type") ?? "LAYER",
                        WorkspaceName = workspaceName
                    });
                }
            }

            var compatibility = AssessLayerGroupCompatibility(groupElement, layers.Count);

            return new GeoServerLayerGroupInfo
            {
                Name = groupElement.GetProperty("name").GetString() ?? string.Empty,
                WorkspaceName = workspaceName ?? string.Empty,
                Title = GetOptionalStringProperty(groupElement, "title"),
                Abstract = GetOptionalStringProperty(groupElement, "abstract"),
                Mode = GetOptionalStringProperty(groupElement, "mode"),
                Layers = layers.ToArray(),
                Enabled = GetOptionalBoolProperty(groupElement, "enabled") ?? true,
                Compatibility = compatibility
            };
        }

        throw new InvalidOperationException($"GeoServer layer group '{workspaceName ?? "global"}/{groupName}' was returned without a layerGroup payload.");
    }

    private async Task<GeoServerStyleInfo[]> GetStylesAsync(
        string baseUrl, GeoServerWorkspaceInfo[] workspaces, bool includeStyleContent, CancellationToken cancellationToken)
    {
        var styles = new List<GeoServerStyleInfo>();

        // Get global styles
        using (var json = await GetRequiredJsonDocumentAsync(BuildJsonUrl(baseUrl, "styles"), cancellationToken).ConfigureAwait(false))
        {
            foreach (var styleElement in EnumerateCollectionItems(json.RootElement, "styles", "style"))
            {
                var styleName = TryGetName(styleElement);
                if (!string.IsNullOrEmpty(styleName))
                {
                    styles.Add(await GetStyleDetailsAsync(baseUrl, null, styleName, includeStyleContent, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        // Get workspace-specific styles
        foreach (var workspace in workspaces)
        {
            using var json = await GetRequiredJsonDocumentAsync(
                BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspace.Name)}/styles"),
                cancellationToken).ConfigureAwait(false);

            foreach (var styleElement in EnumerateCollectionItems(json.RootElement, "styles", "style"))
            {
                var styleName = TryGetName(styleElement);
                if (!string.IsNullOrEmpty(styleName))
                {
                    styles.Add(await GetStyleDetailsAsync(baseUrl, workspace.Name, styleName, includeStyleContent, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return styles.ToArray();
    }

    private async Task<GeoServerStyleInfo> GetStyleDetailsAsync(
        string baseUrl, string? workspaceName, string styleName, bool includeStyleContent, CancellationToken cancellationToken)
    {
        var url = workspaceName == null
            ? BuildJsonUrl(baseUrl, $"styles/{EscapePathSegment(styleName)}")
            : BuildJsonUrl(baseUrl, $"workspaces/{EscapePathSegment(workspaceName)}/styles/{EscapePathSegment(styleName)}");
        using var json = await GetRequiredJsonDocumentAsync(url, cancellationToken).ConfigureAwait(false);

        if (json.RootElement.TryGetProperty("style", out var styleElement))
        {
            string? sldContent = null;

            if (includeStyleContent)
            {
                try
                {
                    var sldUrl = workspaceName == null
                        ? BuildSldUrl(baseUrl, $"styles/{EscapePathSegment(styleName)}")
                        : BuildSldUrl(baseUrl, $"workspaces/{EscapePathSegment(workspaceName)}/styles/{EscapePathSegment(styleName)}");

                    sldContent = await _httpClient.GetStringAsync(sldUrl, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.StyleContentFetchFailed(_logger, styleName, ex);
                }
            }

            var format = GetOptionalStringProperty(styleElement, "format") ?? "sld";
            var compatibility = AssessStyleCompatibility(format, sldContent);

            return new GeoServerStyleInfo
            {
                Name = styleElement.GetProperty("name").GetString() ?? string.Empty,
                WorkspaceName = workspaceName,
                Filename = GetOptionalStringProperty(styleElement, "filename"),
                Format = format,
                LanguageVersion = GetOptionalStringProperty(styleElement, "languageVersion"),
                SldContent = sldContent,
                DateCreated = GetOptionalDateTimeProperty(styleElement, "dateCreated"),
                DateModified = GetOptionalDateTimeProperty(styleElement, "dateModified"),
                Compatibility = compatibility
            };
        }

        throw new InvalidOperationException($"GeoServer style '{workspaceName ?? "global"}/{styleName}' was returned without a style payload.");
    }

    private async Task<JsonDocument> GetRequiredJsonDocumentAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(response);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"GeoServer returned a non-JSON response for '{url}'.", ex);
        }
    }

    private static string BuildJsonUrl(string baseUrl, string relativePath)
        => $"{baseUrl}/{relativePath}.json";

    private static string BuildXmlUrl(string baseUrl, string relativePath)
        => $"{baseUrl}/{relativePath}.xml";

    private static string BuildSldUrl(string baseUrl, string relativePath)
        => $"{baseUrl}/{relativePath}.sld";

    private static string EscapePathSegment(string value)
        => Uri.EscapeDataString(value);

    private static string? TryGetName(JsonElement element)
        => element.TryGetProperty("name", out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IEnumerable<JsonElement> EnumerateCollectionItems(
        JsonElement parent,
        string collectionPropertyName,
        string itemPropertyName)
    {
        if (!parent.TryGetProperty(collectionPropertyName, out var collectionElement) ||
            collectionElement.ValueKind != JsonValueKind.Object ||
            !collectionElement.TryGetProperty(itemPropertyName, out var itemsElement))
        {
            yield break;
        }

        switch (itemsElement.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in itemsElement.EnumerateArray())
                {
                    yield return item;
                }

                yield break;

            case JsonValueKind.String when string.IsNullOrWhiteSpace(itemsElement.GetString()):
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                yield break;

            default:
                yield return itemsElement;
                yield break;
        }
    }

    private static string? GetNamedObjectNameProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        if (property.ValueKind == JsonValueKind.Object)
        {
            return TryGetName(property);
        }

        return null;
    }

    private static string ResolveDataStoreType(
        JsonElement dataStoreElement,
        Dictionary<string, object> connectionParams)
    {
        var explicitType = GetOptionalStringProperty(dataStoreElement, "type");
        if (!string.IsNullOrWhiteSpace(explicitType))
        {
            return explicitType;
        }

        if (connectionParams.TryGetValue("dbtype", out var dbTypeValue) &&
            dbTypeValue is string dbType &&
            !string.IsNullOrWhiteSpace(dbType))
        {
            return dbType switch
            {
                "geopkg" => "GeoPackage",
                _ => dbType
            };
        }

        if (connectionParams.TryGetValue("url", out var urlValue) &&
            urlValue is string url &&
            !string.IsNullOrWhiteSpace(url))
        {
            if (url.EndsWith(".shp", StringComparison.OrdinalIgnoreCase))
            {
                return "Shapefile";
            }

            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return "Directory of spatial files (shapefiles)";
            }
        }

        return string.Empty;
    }

    private static (string? DataStoreName, string? CoverageStoreName) ExtractStoreNames(JsonElement layerElement)
    {
        if (!layerElement.TryGetProperty("resource", out var resourceElement))
        {
            return (null, null);
        }

        var href = GetOptionalStringProperty(resourceElement, "href");
        if (string.IsNullOrWhiteSpace(href))
        {
            return (null, null);
        }

        var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("datastores", StringComparison.OrdinalIgnoreCase))
            {
                return (Uri.UnescapeDataString(segments[i + 1]), null);
            }

            if (segments[i].Equals("coveragestores", StringComparison.OrdinalIgnoreCase))
            {
                return (null, Uri.UnescapeDataString(segments[i + 1]));
            }
        }

        return (null, null);
    }

    // Helper methods for JSON property extraction
    private static string? GetOptionalStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? GetOptionalBoolProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            ? property.GetBoolean()
            : null;
    }

    private static int? GetOptionalIntProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;
    }

    private static DateTimeOffset? GetOptionalDateTimeProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            var dateString = property.GetString();
            if (!string.IsNullOrEmpty(dateString) && DateTimeOffset.TryParse(dateString, out var date))
            {
                return date;
            }
        }
        return null;
    }

    // Compatibility assessment methods
    private static GeoServerResourceCompatibility AssessDataStoreCompatibility(string type, IReadOnlyDictionary<string, object> connectionParams)
    {
        var supportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PostGIS", "PostgreSQL", "Oracle", "MySQL", "SQL Server", "H2", "SQLite",
            "Shapefile", "GeoJSON", "CSV", "Directory of spatial files (shapefiles)",
            "OGC:WFS", "Web Feature Server (NG)"
        };

        if (supportedTypes.Contains(type))
        {
            return new GeoServerResourceCompatibility
            {
                CompatibilityLevel = GeoServerCompatibilityLevel.FullyCompatible,
                Reason = $"DataStore type '{type}' is fully supported by Honua"
            };
        }

        return new GeoServerResourceCompatibility
        {
            CompatibilityLevel = GeoServerCompatibilityLevel.Incompatible,
            Reason = $"DataStore type '{type}' is not currently supported by Honua",
            RequiredManualSteps = [$"Manual migration required for {type} datastore"]
        };
    }

    private static GeoServerResourceCompatibility AssessCoverageStoreCompatibility(string type, IReadOnlyDictionary<string, object> connectionParams)
    {
        var supportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GeoTIFF", "ArcGrid", "GTOPO30", "ImageMosaic", "WorldImage"
        };

        if (supportedTypes.Contains(type))
        {
            return new GeoServerResourceCompatibility
            {
                CompatibilityLevel = GeoServerCompatibilityLevel.PartiallyCompatible,
                Reason = $"CoverageStore type '{type}' may require additional configuration in Honua",
                Warnings = ["Raster support in Honua may have different capabilities than GeoServer"]
            };
        }

        return new GeoServerResourceCompatibility
        {
            CompatibilityLevel = GeoServerCompatibilityLevel.Incompatible,
            Reason = $"CoverageStore type '{type}' is not currently supported by Honua",
            RequiredManualSteps = [$"Manual migration required for {type} coverage store"]
        };
    }

    private static GeoServerResourceCompatibility AssessLayerCompatibility(JsonElement layerElement)
    {
        var enabled = GetOptionalBoolProperty(layerElement, "enabled") ?? true;
        var queryable = GetOptionalBoolProperty(layerElement, "queryable") ?? true;

        if (!enabled)
        {
            return new GeoServerResourceCompatibility
            {
                CompatibilityLevel = GeoServerCompatibilityLevel.PartiallyCompatible,
                Reason = "Disabled layers can be migrated but will need to be manually enabled",
                Warnings = ["Layer is currently disabled in GeoServer"]
            };
        }

        return new GeoServerResourceCompatibility
        {
            CompatibilityLevel = GeoServerCompatibilityLevel.FullyCompatible,
            Reason = "Standard layer configuration is fully supported"
        };
    }

    private static GeoServerResourceCompatibility AssessLayerGroupCompatibility(JsonElement groupElement, int layerCount)
    {
        if (layerCount == 0)
        {
            return new GeoServerResourceCompatibility
            {
                CompatibilityLevel = GeoServerCompatibilityLevel.PartiallyCompatible,
                Reason = "Empty layer groups can be migrated but provide no functionality",
                Warnings = ["Layer group contains no layers"]
            };
        }

        return new GeoServerResourceCompatibility
        {
            CompatibilityLevel = GeoServerCompatibilityLevel.FullyCompatible,
            Reason = "Layer groups are fully supported"
        };
    }

    private static GeoServerResourceCompatibility AssessStyleCompatibility(string format, string? sldContent)
    {
        if (format.Equals("sld", StringComparison.OrdinalIgnoreCase))
        {
            return new GeoServerResourceCompatibility
            {
                CompatibilityLevel = GeoServerCompatibilityLevel.Incompatible,
                Reason = "SLD styles require conversion to MapLibre format (issue #375)",
                RequiredManualSteps = ["Implement SLD to MapLibre style conversion", "Manual style recreation may be required"]
            };
        }

        return new GeoServerResourceCompatibility
        {
            CompatibilityLevel = GeoServerCompatibilityLevel.Incompatible,
            Reason = $"Style format '{format}' is not supported",
            RequiredManualSteps = [$"Convert {format} style to supported format"]
        };
    }

    private static GeoServerMigrationCompatibility PerformCompatibilityAnalysis(
        GeoServerDataStoreInfo[] dataStores,
        GeoServerCoverageStoreInfo[] coverageStores,
        GeoServerLayerInfo[] layers,
        GeoServerStyleInfo[] styles)
    {
        var fullyCompatible = 0;
        var partiallyCompatible = 0;
        var incompatible = 0;
        var unsupportedFeatures = new List<string>();
        var manualSteps = new List<string>();
        var warnings = new List<string>();

        // Analyze datastores
        foreach (var dataStore in dataStores)
        {
            switch (dataStore.Compatibility?.CompatibilityLevel)
            {
                case GeoServerCompatibilityLevel.FullyCompatible:
                    fullyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.PartiallyCompatible:
                    partiallyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.Incompatible:
                    incompatible++;
                    unsupportedFeatures.Add($"DataStore: {dataStore.Name} (type: {dataStore.Type})");
                    break;
            }
        }

        // Analyze coverage stores
        foreach (var coverageStore in coverageStores)
        {
            switch (coverageStore.Compatibility?.CompatibilityLevel)
            {
                case GeoServerCompatibilityLevel.FullyCompatible:
                    fullyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.PartiallyCompatible:
                    partiallyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.Incompatible:
                    incompatible++;
                    unsupportedFeatures.Add($"CoverageStore: {coverageStore.Name} (type: {coverageStore.Type})");
                    break;
            }
        }

        // Analyze layers
        foreach (var layer in layers)
        {
            switch (layer.Compatibility?.CompatibilityLevel)
            {
                case GeoServerCompatibilityLevel.FullyCompatible:
                    fullyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.PartiallyCompatible:
                    partiallyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.Incompatible:
                    incompatible++;
                    break;
            }
        }

        // Analyze styles
        foreach (var style in styles)
        {
            switch (style.Compatibility?.CompatibilityLevel)
            {
                case GeoServerCompatibilityLevel.FullyCompatible:
                    fullyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.PartiallyCompatible:
                    partiallyCompatible++;
                    break;
                case GeoServerCompatibilityLevel.Incompatible:
                    incompatible++;
                    if (style.Format == "sld")
                    {
                        unsupportedFeatures.Add($"SLD Style: {style.Name} (requires issue #375)");
                    }
                    break;
            }
        }

        if (styles.Length > 0)
        {
            manualSteps.Add("Implement SLD to MapLibre style conversion (issue #375)");
            warnings.Add("Style migration requires implementing issue #375 for SLD conversion");
        }

        if (incompatible > 0)
        {
            manualSteps.Add("Review and manually migrate incompatible resources");
        }

        var resourceTypeBreakdown = new[]
        {
            new GeoServerResourceTypeCompatibility
            {
                ResourceType = "DataStore",
                FullyCompatible = dataStores.Count(d => d.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.FullyCompatible),
                PartiallyCompatible = dataStores.Count(d => d.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.PartiallyCompatible),
                Incompatible = dataStores.Count(d => d.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
            },
            new GeoServerResourceTypeCompatibility
            {
                ResourceType = "CoverageStore",
                FullyCompatible = coverageStores.Count(cs => cs.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.FullyCompatible),
                PartiallyCompatible = coverageStores.Count(cs => cs.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.PartiallyCompatible),
                Incompatible = coverageStores.Count(cs => cs.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
            },
            new GeoServerResourceTypeCompatibility
            {
                ResourceType = "Layer",
                FullyCompatible = layers.Count(l => l.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.FullyCompatible),
                PartiallyCompatible = layers.Count(l => l.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.PartiallyCompatible),
                Incompatible = layers.Count(l => l.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
            },
            new GeoServerResourceTypeCompatibility
            {
                ResourceType = "Style",
                FullyCompatible = styles.Count(s => s.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.FullyCompatible),
                PartiallyCompatible = styles.Count(s => s.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.PartiallyCompatible),
                Incompatible = styles.Count(s => s.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
            }
        };

        return new GeoServerMigrationCompatibility
        {
            FullyCompatibleResources = fullyCompatible,
            PartiallyCompatibleResources = partiallyCompatible,
            IncompatibleResources = incompatible,
            ResourceTypeBreakdown = resourceTypeBreakdown,
            UnsupportedFeatures = unsupportedFeatures.ToArray(),
            RequiredManualSteps = manualSteps.ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    private static partial class Log
    {
        [LoggerMessage(7980, LogLevel.Information, "Discovering GeoServer service at {GeoServerUrl}")]
        public static partial void DiscoveringService(ILogger logger, string geoServerUrl);

        [LoggerMessage(7981, LogLevel.Information, "Successfully discovered GeoServer service: {WorkspaceCount} workspaces, {DataStoreCount} datastores, {LayerCount} layers")]
        public static partial void ServiceDiscovered(ILogger logger, int workspaceCount, int dataStoreCount, int layerCount);

        [LoggerMessage(7982, LogLevel.Error, "HTTP request failed while discovering GeoServer service at {GeoServerUrl}")]
        public static partial void ServiceDiscoveryHttpFailed(ILogger logger, string geoServerUrl, Exception exception);

        [LoggerMessage(7983, LogLevel.Error, "Timeout while discovering GeoServer service at {GeoServerUrl}")]
        public static partial void ServiceDiscoveryTimedOut(ILogger logger, string geoServerUrl, Exception exception);

        [LoggerMessage(7984, LogLevel.Error, "Unexpected error while discovering GeoServer service at {GeoServerUrl}")]
        public static partial void ServiceDiscoveryUnexpectedError(ILogger logger, string geoServerUrl, Exception exception);

        [LoggerMessage(7985, LogLevel.Warning, "Could not retrieve GeoServer version information")]
        public static partial void VersionUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(7986, LogLevel.Warning, "Could not retrieve GeoServer global settings")]
        public static partial void GlobalSettingsUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(7987, LogLevel.Warning, "Failed to fetch SLD content for style {StyleName}")]
        public static partial void StyleContentFetchFailed(ILogger logger, string styleName, Exception exception);
    }
}

/// <summary>
/// Activity tracking for GeoServer import operations.
/// </summary>
internal static partial class GeoServerImportActivity
{
    private static readonly System.Diagnostics.ActivitySource ActivitySource = new("Honua.GeoServer.Import");

    public static System.Diagnostics.Activity? StartDiscovery(string geoServerUrl)
        => ActivitySource.StartActivity("geoserver.discovery")?.SetTag("geoserver.url", geoServerUrl);

    public static System.Diagnostics.Activity? StartImport(string geoServerUrl, string targetUrl)
        => ActivitySource.StartActivity("geoserver.import")
            ?.SetTag("geoserver.url", geoServerUrl)
            ?.SetTag("target.url", targetUrl);
}
