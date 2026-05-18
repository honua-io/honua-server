// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// HTTP-backed scanner for OGC service migration inventories.
/// </summary>
public sealed partial class OgcServiceMigrationScanner : IOgcServiceMigrationScanner
{
    private const string DisallowedNetworkAddressMessage = "OGC service URL resolves to a disallowed network address.";

    private readonly HttpClient _httpClient;
    private readonly ICrsRegistry _crsRegistry;
    private readonly ILogger<OgcServiceMigrationScanner> _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostAddressResolver;
    private static readonly AsyncLocal<bool> UnsafeLocalUrlsAllowed = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OgcServiceMigrationScanner"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client used for service discovery.</param>
    /// <param name="crsRegistry">CRS registry used to normalize advertised spatial references.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="hostAddressResolver">Optional host resolver used by tests.</param>
    public OgcServiceMigrationScanner(
        HttpClient httpClient,
        ICrsRegistry crsRegistry,
        ILogger<OgcServiceMigrationScanner> logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostAddressResolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    /// <summary>
    /// Creates an HTTP handler that pins DNS resolution before connecting to an OGC source host.
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
        OgcServiceScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedServiceType = NormalizeServiceType(request.ServiceType);
        var sourceKind = $"ogc-{normalizedServiceType.ToLowerInvariant()}";
        var serviceUri = ValidateServiceUri(request.ServiceUrl, request.AllowUnsafeLocalUrls);

        try
        {
            _ = await ResolveAllowedAddressesAsync(
                    serviceUri.DnsSafeHost,
                    _hostAddressResolver,
                    request.AllowUnsafeLocalUrls,
                    cancellationToken)
                .ConfigureAwait(false);

            var capabilitiesUrl = BuildCapabilitiesUrl(serviceUri, normalizedServiceType, request.Version);
            var previousUnsafeLocalUrlsAllowed = UnsafeLocalUrlsAllowed.Value;
            UnsafeLocalUrlsAllowed.Value = request.AllowUnsafeLocalUrls;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds <= 0 ? 60 : request.TimeoutSeconds));

                var capabilitiesXml = await _httpClient.GetStringAsync(capabilitiesUrl, timeout.Token).ConfigureAwait(false);
                var capabilities = XDocument.Parse(capabilitiesXml, LoadOptions.None);
                return normalizedServiceType switch
                {
                    "WFS" => await BuildWfsInventoryAsync(request, serviceUri, capabilitiesUrl, capabilities, timeout.Token).ConfigureAwait(false),
                    "WMS" => BuildWmsInventory(request, serviceUri, capabilitiesUrl, capabilities),
                    "WMTS" => BuildWmtsInventory(request, serviceUri, capabilitiesUrl, capabilities),
                    _ => throw new InvalidOperationException("Unsupported OGC service type.")
                };
            }
            finally
            {
                UnsafeLocalUrlsAllowed.Value = previousUnsafeLocalUrlsAllowed;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Xml.XmlException ||
                                   ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Log.ScanFailed(_logger, sourceKind, ToSafeSourceUrl(serviceUri), ex);
            return CreateFailedArtifact(sourceKind, serviceUri, normalizedServiceType, ToSafeScanFailureReason(ex));
        }
    }

    private async Task<MigrationSourceInventoryArtifact> BuildWfsInventoryAsync(
        OgcServiceScanRequest request,
        Uri serviceUri,
        Uri capabilitiesUrl,
        XDocument capabilities,
        CancellationToken cancellationToken)
    {
        var version = GetRootVersion(capabilities) ?? request.Version ?? "unknown";
        var displayName = GetServiceTitle(capabilities) ?? serviceUri.Host;
        var containerId = "service:wfs";
        var featureTypes = Descendants(capabilities, "FeatureType")
            .Where(static element => ChildValue(element, "Name") != null)
            .OrderBy(static element => ChildValue(element, "Name"), StringComparer.Ordinal)
            .ToArray();

        var resources = new List<MigrationInventoryResource>(featureTypes.Length);
        var warnings = new List<string>();
        var missingArtifacts = new List<string>();

        foreach (var featureType in featureTypes)
        {
            var name = ChildValue(featureType, "Name")!;
            var resourceId = $"feature-type:{ToStableId(name)}";
            var crsValues = ReadWfsCrsValues(featureType);
            var spatialReferences = await BuildSpatialReferencesAsync(crsValues, cancellationToken).ConfigureAwait(false);
            var schema = await TryDescribeFeatureTypeAsync(serviceUri, request.Version ?? version, name, request.TimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            if (!schema.Success)
            {
                warnings.Add($"DescribeFeatureType metadata was unavailable for {name}: {schema.Warning}");
                missingArtifacts.Add($"schema:{name}");
            }

            var geometryType = schema.Fields.FirstOrDefault(static field => IsGmlGeometryType(field.FieldType))?.FieldType;
            resources.Add(new MigrationInventoryResource
            {
                Id = resourceId,
                ContainerId = containerId,
                Kind = "feature-type",
                Name = name,
                Title = ChildValue(featureType, "Title"),
                Description = ChildValue(featureType, "Abstract"),
                GeometryType = NormalizeGeometryType(geometryType),
                Capabilities = ["wfs:GetCapabilities", "wfs:DescribeFeatureType", "wfs:GetFeature"],
                SpatialReferences = spatialReferences,
                Fields = schema.Fields,
                Compatibility = schema.Success
                    ? MigrationInventoryHelpers.Compatible(
                        "WFS feature type metadata can be represented in the migration inventory.",
                        code: ImportCompatibilityCodes.OgcWfsFeatureSource)
                    : MigrationInventoryHelpers.Partial(
                        "WFS feature type is discoverable, but schema metadata needs manual review.",
                        [schema.Warning],
                        ["Run DescribeFeatureType manually and confirm field and geometry mappings before import."],
                        ImportCompatibilityCodes.OgcFeatureSchemaManualReview)
            });
        }

        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = containerId,
                Kind = "ogc-service",
                Name = "WFS",
                Title = displayName,
                IsDefault = true,
                Compatibility = MigrationInventoryHelpers.Aggregate(
                    resources.Select(static resource => resource.Compatibility),
                    "No WFS feature types were advertised.")
            }
        };

        var dependencies = new[]
        {
            new MigrationExternalDependency
            {
                Id = "endpoint:wfs:get-capabilities",
                ContainerId = containerId,
                Kind = "ogc-endpoint",
                Name = "WFS GetCapabilities",
                DependencyType = "capabilities",
                Address = ToSafeCapabilitiesUrl(capabilitiesUrl),
                Metadata = new Dictionary<string, string>
                {
                    ["service"] = "WFS",
                    ["version"] = version
                },
                Compatibility = MigrationInventoryHelpers.Compatible(
                    "WFS capabilities endpoint was captured for migration planning.",
                    code: ImportCompatibilityCodes.OgcWfsFeatureSource)
            }
        };

        return CreateArtifact(
            "ogc-wfs",
            displayName,
            serviceUri,
            "OGC Web Feature Service",
            version,
            "WFS",
            containers,
            resources.ToArray(),
            [],
            dependencies,
            warnings.ToArray(),
            missingArtifacts.ToArray());
    }

    private static MigrationSourceInventoryArtifact BuildWmsInventory(
        OgcServiceScanRequest request,
        Uri serviceUri,
        Uri capabilitiesUrl,
        XDocument capabilities)
    {
        var version = GetRootVersion(capabilities) ?? request.Version ?? "unknown";
        var displayName = GetServiceTitle(capabilities) ?? serviceUri.Host;
        var containerId = "service:wms";
        var layers = Descendants(capabilities, "Layer")
            .Where(static element => ChildValue(element, "Name") != null)
            .OrderBy(static element => ChildValue(element, "Name"), StringComparer.Ordinal)
            .ToArray();

        var resources = layers.Select(layer =>
        {
            var name = ChildValue(layer, "Name")!;
            return new MigrationInventoryResource
            {
                Id = $"wms-layer:{ToStableId(name)}",
                ContainerId = containerId,
                Kind = "render-layer",
                Name = name,
                Title = ChildValue(layer, "Title"),
                Description = ChildValue(layer, "Abstract"),
                Capabilities = ["wms:GetCapabilities", "wms:GetMap", "wms:GetFeatureInfo"],
                Compatibility = MigrationInventoryHelpers.Incompatible(
                    "WMS exposes rendered map images and cannot supply automated feature data-copy by itself.",
                    manualSteps: ["Pair this WMS layer with a WFS, coverage, database, or file source before planning data import."],
                    code: ImportCompatibilityCodes.OgcWmsRenderOnlySource)
            };
        }).ToArray();

        var styles = layers
            .SelectMany(layer => BuildWmsStyles(containerId, layer))
            .OrderBy(static style => style.Id, StringComparer.Ordinal)
            .ToArray();

        return CreateRenderOnlyArtifact(
            "ogc-wms",
            displayName,
            serviceUri,
            "OGC Web Map Service",
            version,
            "WMS",
            containerId,
            capabilitiesUrl,
            resources,
            styles,
            ImportCompatibilityCodes.OgcWmsRenderOnlySource);
    }

    private static MigrationSourceInventoryArtifact BuildWmtsInventory(
        OgcServiceScanRequest request,
        Uri serviceUri,
        Uri capabilitiesUrl,
        XDocument capabilities)
    {
        var version = GetRootVersion(capabilities) ?? request.Version ?? "1.0.0";
        var displayName = GetServiceTitle(capabilities) ?? serviceUri.Host;
        var containerId = "service:wmts";
        var layers = Descendants(capabilities, "Layer")
            .Where(static element => ChildValue(element, "Identifier") != null)
            .OrderBy(static element => ChildValue(element, "Identifier"), StringComparer.Ordinal)
            .ToArray();

        var resources = layers.Select(layer =>
        {
            var identifier = ChildValue(layer, "Identifier")!;
            return new MigrationInventoryResource
            {
                Id = $"wmts-layer:{ToStableId(identifier)}",
                ContainerId = containerId,
                Kind = "tile-layer",
                Name = identifier,
                Title = ChildValue(layer, "Title"),
                Description = ChildValue(layer, "Abstract"),
                Capabilities = ["wmts:GetCapabilities", "wmts:GetTile"],
                ExternalDependencyIds = Descendants(layer, "TileMatrixSet")
                    .Select(static element => element.Value.Trim())
                    .Where(static value => value.Length > 0)
                    .Select(static value => $"tile-matrix-set:{ToStableId(value)}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray(),
                Compatibility = MigrationInventoryHelpers.Incompatible(
                    "WMTS exposes pre-rendered tiles and cannot supply automated feature data-copy by itself.",
                    manualSteps: ["Pair this WMTS layer with a WFS, coverage, database, or file source before planning data import."],
                    code: ImportCompatibilityCodes.OgcWmtsTileOnlySource)
            };
        }).ToArray();

        var styles = layers
            .SelectMany(layer => BuildWmtsStyles(containerId, layer))
            .OrderBy(static style => style.Id, StringComparer.Ordinal)
            .ToArray();
        var dependencies = Descendants(capabilities, "TileMatrixSet")
            .Where(static element => element.Parent?.Name.LocalName == "Contents")
            .Select(element =>
            {
                var identifier = ChildValue(element, "Identifier") ?? "unknown";
                return new MigrationExternalDependency
                {
                    Id = $"tile-matrix-set:{ToStableId(identifier)}",
                    ContainerId = containerId,
                    Kind = "tile-matrix-set",
                    Name = identifier,
                    DependencyType = "WMTS TileMatrixSet",
                    Compatibility = MigrationInventoryHelpers.Partial(
                        "WMTS tile matrix set metadata was captured for manual service migration planning.",
                        [],
                        ["Review tile matrix set compatibility with Honua cache/grid configuration."],
                        ImportCompatibilityCodes.OgcWmtsTileOnlySource)
                };
            })
            .OrderBy(static dependency => dependency.Id, StringComparer.Ordinal)
            .ToArray();

        return CreateRenderOnlyArtifact(
            "ogc-wmts",
            displayName,
            serviceUri,
            "OGC Web Map Tile Service",
            version,
            "WMTS",
            containerId,
            capabilitiesUrl,
            resources,
            styles,
            ImportCompatibilityCodes.OgcWmtsTileOnlySource,
            dependencies);
    }

    private static MigrationSourceInventoryArtifact CreateRenderOnlyArtifact(
        string sourceKind,
        string displayName,
        Uri serviceUri,
        string product,
        string version,
        string serviceType,
        string containerId,
        Uri capabilitiesUrl,
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[] styles,
        string compatibilityCode,
        MigrationExternalDependency[]? extraDependencies = null)
    {
        var dependencies = new List<MigrationExternalDependency>
        {
            new()
            {
                Id = $"endpoint:{serviceType.ToLowerInvariant()}:get-capabilities",
                ContainerId = containerId,
                Kind = "ogc-endpoint",
                Name = $"{serviceType} GetCapabilities",
                DependencyType = "capabilities",
                Address = ToSafeCapabilitiesUrl(capabilitiesUrl),
                Metadata = new Dictionary<string, string>
                {
                    ["service"] = serviceType,
                    ["version"] = version
                },
                Compatibility = MigrationInventoryHelpers.Partial(
                    $"{serviceType} capabilities endpoint was captured for manual service migration planning.",
                    [],
                    ["Review render, style, and cache metadata before publishing equivalent Honua services."],
                    compatibilityCode)
            }
        };

        if (extraDependencies != null)
        {
            dependencies.AddRange(extraDependencies);
        }

        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = containerId,
                Kind = "ogc-service",
                Name = serviceType,
                Title = displayName,
                IsDefault = true,
                Compatibility = MigrationInventoryHelpers.Aggregate(
                    resources.Select(static resource => resource.Compatibility)
                        .Concat(styles.Select(static style => style.Compatibility))
                        .Concat(dependencies.Select(static dependency => dependency.Compatibility)),
                    $"No {serviceType} layers were advertised.")
            }
        };

        return CreateArtifact(
            sourceKind,
            displayName,
            serviceUri,
            product,
            version,
            serviceType,
            containers,
            resources,
            styles,
            dependencies.OrderBy(static dependency => dependency.Id, StringComparer.Ordinal).ToArray(),
            [],
            []);
    }

    private static MigrationSourceInventoryArtifact CreateArtifact(
        string sourceKind,
        string displayName,
        Uri serviceUri,
        string product,
        string version,
        string serviceType,
        MigrationInventoryContainer[] containers,
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[] styles,
        MigrationExternalDependency[] dependencies,
        string[] warnings,
        string[] missingArtifacts)
    {
        var summary = MigrationInventoryHelpers.BuildSummary(containers, resources, styles, dependencies);
        var overallCompatibility = MigrationInventoryHelpers.Aggregate(
            containers.Select(static container => container.Compatibility)
                .Concat(resources.Select(static resource => resource.Compatibility))
                .Concat(styles.Select(static style => style.Compatibility))
                .Concat(dependencies.Select(static dependency => dependency.Compatibility)),
            "No OGC inventory items were discovered.");

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = sourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = displayName,
                BaseUrl = ToSafeSourceUrl(serviceUri),
                Product = product,
                Version = version,
                ServiceType = serviceType
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous",
                CredentialsSupplied = false,
                AccessConfirmed = true
            },
            ScanCompleteness = MigrationInventoryHelpers.BuildCompleteness(
                warnings.Length == 0 ? "complete" : "partial",
                warnings,
                missingArtifacts),
            Summary = summary,
            OverallCompatibility = overallCompatibility,
            Containers = containers,
            Resources = resources.OrderBy(static resource => resource.Id, StringComparer.Ordinal).ToArray(),
            Styles = styles.OrderBy(static style => style.Id, StringComparer.Ordinal).ToArray(),
            ExternalDependencies = dependencies.OrderBy(static dependency => dependency.Id, StringComparer.Ordinal).ToArray()
        };
    }

    private static MigrationSourceInventoryArtifact CreateFailedArtifact(
        string sourceKind,
        Uri serviceUri,
        string serviceType,
        string reason)
        => new()
        {
            SourceKind = sourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = $"OGC {serviceType}",
                BaseUrl = ToSafeSourceUrl(serviceUri),
                Product = $"OGC {serviceType}",
                ServiceType = serviceType
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
                "The OGC service scan did not complete successfully.",
                [reason],
                ["Verify OGC endpoint reachability, service type, version, and access requirements, then rerun the scan."],
                ImportCompatibilityCodes.OgcScanFailed)
        };

    private async Task<(bool Success, MigrationInventoryField[] Fields, string Warning)> TryDescribeFeatureTypeAsync(
        Uri serviceUri,
        string version,
        string featureTypeName,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildDescribeFeatureTypeUrl(serviceUri, version, featureTypeName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 60 : timeoutSeconds));
            var schemaXml = await _httpClient.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            var schema = XDocument.Parse(schemaXml, LoadOptions.None);
            var fields = ExtractFields(schema, featureTypeName);
            return (fields.Length > 0, fields, fields.Length > 0 ? string.Empty : "DescribeFeatureType returned no importable field metadata.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return (false, [], ToSafeDescribeFeatureTypeFailureReason(ex));
        }
    }

    private static string ToSafeScanFailureReason(Exception exception)
        => exception switch
        {
            TaskCanceledException => "OGC service scan timed out.",
            HttpRequestException => "OGC service endpoint could not be reached or returned an unsupported response.",
            System.Xml.XmlException => "OGC service capabilities metadata could not be parsed.",
            _ => "OGC service metadata could not be scanned."
        };

    private static string ToSafeDescribeFeatureTypeFailureReason(Exception exception)
        => exception switch
        {
            TaskCanceledException => "DescribeFeatureType request timed out.",
            HttpRequestException => "DescribeFeatureType metadata could not be retrieved.",
            System.Xml.XmlException => "DescribeFeatureType metadata could not be parsed.",
            _ => "DescribeFeatureType metadata was unavailable."
        };

    private static MigrationInventoryField[] ExtractFields(XDocument schema, string featureTypeName)
    {
        var featureTypeLocalName = LocalName(featureTypeName);
        return Descendants(schema, "element")
            .Where(element => element.Attribute("name")?.Value is { Length: > 0 })
            .Select(element => new
            {
                Name = element.Attribute("name")!.Value,
                Type = element.Attribute("type")?.Value,
                SubstitutionGroup = element.Attribute("substitutionGroup")?.Value,
                MinOccurs = element.Attribute("minOccurs")?.Value,
                Nillable = element.Attribute("nillable")?.Value
            })
            .Where(element => !string.Equals(element.Name, featureTypeLocalName, StringComparison.Ordinal) &&
                              !IsFeatureDeclaration(element.Type, element.SubstitutionGroup))
            .Select(element => new MigrationInventoryField
            {
                Name = element.Name,
                FieldType = element.Type ?? "unknown",
                Nullable = string.Equals(element.MinOccurs, "0", StringComparison.Ordinal) ||
                           string.Equals(element.Nillable, "true", StringComparison.OrdinalIgnoreCase)
            })
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static MigrationInventoryStyle[] BuildWmsStyles(string containerId, XElement layer)
    {
        var layerName = ChildValue(layer, "Name");
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return [];
        }

        var resourceId = $"wms-layer:{ToStableId(layerName)}";
        return Elements(layer, "Style")
            .Select(style =>
            {
                var styleName = ChildValue(style, "Name") ?? "default";
                return new MigrationInventoryStyle
                {
                    Id = $"style:{ToStableId(layerName)}:{ToStableId(styleName)}",
                    ContainerId = containerId,
                    Kind = "wms-style",
                    Name = styleName,
                    Format = "WMS",
                    ResourceIds = [resourceId],
                    Compatibility = MigrationInventoryHelpers.Partial(
                        "WMS style metadata was captured for manual render-service migration planning.",
                        [],
                        ["Review WMS style semantics and recreate equivalent Honua styles where required."],
                        ImportCompatibilityCodes.OgcWmsRenderOnlySource)
                };
            })
            .ToArray();
    }

    private static MigrationInventoryStyle[] BuildWmtsStyles(string containerId, XElement layer)
    {
        var layerId = ChildValue(layer, "Identifier");
        if (string.IsNullOrWhiteSpace(layerId))
        {
            return [];
        }

        var resourceId = $"wmts-layer:{ToStableId(layerId)}";
        return Elements(layer, "Style")
            .Select(style =>
            {
                var styleName = ChildValue(style, "Identifier") ?? "default";
                return new MigrationInventoryStyle
                {
                    Id = $"style:{ToStableId(layerId)}:{ToStableId(styleName)}",
                    ContainerId = containerId,
                    Kind = "wmts-style",
                    Name = styleName,
                    Format = "WMTS",
                    ResourceIds = [resourceId],
                    Compatibility = MigrationInventoryHelpers.Partial(
                        "WMTS style metadata was captured for manual tile-service migration planning.",
                        [],
                        ["Review WMTS style semantics and recreate equivalent Honua styles where required."],
                        ImportCompatibilityCodes.OgcWmtsTileOnlySource)
                };
            })
            .ToArray();
    }

    private async Task<MigrationSpatialReferenceInfo[]> BuildSpatialReferencesAsync(
        IReadOnlyList<(string Role, string? Value)> crsValues,
        CancellationToken cancellationToken)
    {
        var spatialReferences = new List<MigrationSpatialReferenceInfo>();
        foreach (var (role, value) in crsValues)
        {
            var info = await MigrationInventoryHelpers.BuildSpatialReferenceAsync(
                    _crsRegistry,
                    role,
                    value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (info != null &&
                !spatialReferences.Any(existing =>
                    string.Equals(existing.Role, info.Role, StringComparison.Ordinal) &&
                    string.Equals(existing.SourceValue, info.SourceValue, StringComparison.Ordinal)))
            {
                spatialReferences.Add(info);
            }
        }

        return spatialReferences
            .OrderBy(static info => info.Role, StringComparer.Ordinal)
            .ThenBy(static info => info.SourceValue, StringComparer.Ordinal)
            .ToArray();
    }

    private static List<(string Role, string? Value)> ReadWfsCrsValues(XElement featureType)
    {
        var values = new List<(string Role, string? Value)>();
        var declared = ChildValue(featureType, "DefaultCRS") ??
                       ChildValue(featureType, "DefaultSRS") ??
                       ChildValue(featureType, "SRS");
        values.Add(("declared", declared));
        values.AddRange(Elements(featureType, "OtherCRS").Select(static element => ("other", (string?)element.Value.Trim())));
        values.AddRange(Elements(featureType, "OtherSRS").Select(static element => ("other", (string?)element.Value.Trim())));
        return values;
    }

    private static string NormalizeServiceType(string serviceType)
    {
        var normalized = serviceType.Trim().ToUpperInvariant();
        return normalized is "WFS" or "WMS" or "WMTS"
            ? normalized
            : throw new InvalidOperationException("Unsupported OGC service type.");
    }

    private static Uri ValidateServiceUri(string serviceUrl, bool allowUnsafeLocalUrls)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            (allowUnsafeLocalUrls
                ? uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                : uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpRequestException(allowUnsafeLocalUrls
                ? "OGC service URL must be a valid HTTP or HTTPS URL."
                : "OGC service URL must be a valid HTTPS URL.");
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new HttpRequestException("OGC service URL must not include embedded credentials.");
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

        throw new HttpRequestException("Unable to establish a connection to the OGC service host.", lastException);
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
                   (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
                   bytes[0] >= 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var bytesV6 = address.GetAddressBytes();
        return address.Equals(IPAddress.IPv6None) ||
               address.Equals(IPAddress.IPv6Loopback) ||
               address.IsIPv6LinkLocal ||
               address.IsIPv6SiteLocal ||
               address.IsIPv6Multicast ||
               (bytesV6[0] & 0xfe) == 0xfc ||
               (bytesV6[0] == 0x20 && bytesV6[1] == 0x01 && bytesV6[2] == 0x0d && bytesV6[3] == 0xb8);
    }

    private static Uri BuildCapabilitiesUrl(Uri serviceUri, string serviceType, string? version)
    {
        if (HasQueryParameter(serviceUri, "request", "GetCapabilities"))
        {
            return serviceUri;
        }

        var builder = new UriBuilder(serviceUri);
        var query = BuildQuery(builder.Query, new Dictionary<string, string?>
        {
            ["service"] = serviceType,
            ["request"] = "GetCapabilities",
            ["version"] = version
        });
        builder.Query = query;
        return builder.Uri;
    }

    private static Uri BuildDescribeFeatureTypeUrl(Uri serviceUri, string version, string featureTypeName)
    {
        var builder = new UriBuilder(serviceUri)
        {
            Query = BuildQuery(
                serviceUri.Query,
                new Dictionary<string, string?>
                {
                    ["service"] = "WFS",
                    ["version"] = version.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : version,
                    ["request"] = "DescribeFeatureType",
                    [version.StartsWith("2.", StringComparison.Ordinal) ? "typeNames" : "typeName"] = featureTypeName
                })
        };
        return builder.Uri;
    }

    private static string BuildQuery(string existingQuery, IReadOnlyDictionary<string, string?> values)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var trimmed = existingQuery.TrimStart('?');
        if (trimmed.Length > 0)
        {
            foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                var key = separator < 0 ? Uri.UnescapeDataString(part) : Uri.UnescapeDataString(part[..separator]);
                if (values.Keys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return string.Join("&", pairs.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static bool HasQueryParameter(Uri uri, string name, string value)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return false;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? Uri.UnescapeDataString(part) : Uri.UnescapeDataString(part[..separator]);
            var candidateValue = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidateValue, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static string ToSafeCapabilitiesUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Fragment = string.Empty,
            Query = BuildSafeCapabilitiesQuery(uri.Query)
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string BuildSafeCapabilitiesQuery(string query)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? Uri.UnescapeDataString(part) : Uri.UnescapeDataString(part[..separator]);
            if (IsSensitiveCapabilitiesQueryParameter(key))
            {
                continue;
            }

            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
            pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        return string.Join("&", pairs
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static bool IsSensitiveCapabilitiesQueryParameter(string key)
        => key.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("auth", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("client_secret", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("credential", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("key", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("passwd", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("pwd", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("session", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("signature", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("sig", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("token", StringComparison.OrdinalIgnoreCase);

    private static string? GetRootVersion(XDocument document)
        => document.Root?.Attribute("version")?.Value;

    private static string? GetServiceTitle(XDocument document)
        => Descendants(document, "ServiceIdentification").Select(static element => ChildValue(element, "Title")).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
           Descendants(document, "Service").Select(static element => ChildValue(element, "Title")).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static IEnumerable<XElement> Descendants(XContainer container, string localName)
        => container.Descendants().Where(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal));

    private static IEnumerable<XElement> Elements(XElement element, string localName)
        => element.Elements().Where(child => string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal));

    private static string? ChildValue(XElement element, string localName)
        => Elements(element, localName)
            .Select(static child => child.Value.Trim())
            .FirstOrDefault(static value => value.Length > 0);

    private static string ToStableId(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        var lastWasSeparator = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && length > 0)
            {
                buffer[length++] = '-';
                lastWasSeparator = true;
            }
        }

        while (length > 0 && buffer[length - 1] == '-')
        {
            length--;
        }

        return length == 0 ? "unnamed" : new string(buffer, 0, length);
    }

    private static string LocalName(string value)
    {
        var separator = value.LastIndexOf(':');
        return separator >= 0 && separator + 1 < value.Length ? value[(separator + 1)..] : value;
    }

    private static bool IsFeatureDeclaration(string? type, string? substitutionGroup)
        => (type?.EndsWith("Type", StringComparison.Ordinal) == true &&
            substitutionGroup?.Contains("Feature", StringComparison.OrdinalIgnoreCase) == true) ||
           substitutionGroup?.EndsWith(":_Feature", StringComparison.OrdinalIgnoreCase) == true ||
           substitutionGroup?.EndsWith(":AbstractFeature", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsGmlGeometryType(string? type)
        => type?.Contains("gml:", StringComparison.OrdinalIgnoreCase) == true &&
           type.Contains("PropertyType", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeGeometryType(string? fieldType)
    {
        if (string.IsNullOrWhiteSpace(fieldType))
        {
            return null;
        }

        var localType = LocalName(fieldType)
            .Replace("PropertyType", string.Empty, StringComparison.Ordinal)
            .Replace("Property", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(localType) ? null : localType;
    }

    private static partial class Log
    {
        [LoggerMessage(22101, LogLevel.Warning, "OGC migration scan failed for {SourceKind} source {SourceUrl}")]
        public static partial void ScanFailed(ILogger logger, string sourceKind, string sourceUrl, Exception exception);
    }
}
