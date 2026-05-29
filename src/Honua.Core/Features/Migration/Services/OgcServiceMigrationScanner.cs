// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml.Linq;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

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
        var sourceKind = GetSourceKind(normalizedServiceType);
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

                return normalizedServiceType switch
                {
                    "OGC API Coverages" => await BuildOgcApiCoveragesInventoryAsync(
                            request,
                            serviceUri,
                            timeout.Token)
                        .ConfigureAwait(false),
                    _ => await BuildXmlBackedInventoryAsync(
                            request,
                            normalizedServiceType,
                            serviceUri,
                            capabilitiesUrl,
                            timeout.Token)
                        .ConfigureAwait(false)
                };
            }
            finally
            {
                UnsafeLocalUrlsAllowed.Value = previousUnsafeLocalUrlsAllowed;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Xml.XmlException ||
                                   ex is JsonException ||
                                   ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Log.ScanFailed(_logger, sourceKind, ToSafeSourceUrl(serviceUri), ex);
            return CreateFailedArtifact(sourceKind, serviceUri, normalizedServiceType, ToSafeScanFailureReason(ex));
        }
    }

    private async Task<MigrationSourceInventoryArtifact> BuildXmlBackedInventoryAsync(
        OgcServiceScanRequest request,
        string normalizedServiceType,
        Uri serviceUri,
        Uri capabilitiesUrl,
        CancellationToken cancellationToken)
    {
        var capabilitiesXml = await _httpClient.GetStringAsync(capabilitiesUrl, cancellationToken).ConfigureAwait(false);
        var capabilities = XDocument.Parse(capabilitiesXml, LoadOptions.None);
        return normalizedServiceType switch
        {
            "WFS" => await BuildWfsInventoryAsync(request, serviceUri, capabilitiesUrl, capabilities, cancellationToken).ConfigureAwait(false),
            "WMS" => BuildWmsInventory(request, serviceUri, capabilitiesUrl, capabilities),
            "WMTS" => BuildWmtsInventory(request, serviceUri, capabilitiesUrl, capabilities),
            "WCS" => await BuildWcsInventoryAsync(request, serviceUri, capabilities, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported OGC service type.")
        };
    }

    private async Task<MigrationSourceInventoryArtifact> BuildWcsInventoryAsync(
        OgcServiceScanRequest request,
        Uri serviceUri,
        XDocument capabilities,
        CancellationToken cancellationToken)
    {
        var version = GetRootVersion(capabilities) ?? request.Version ?? "unknown";
        var serviceMetadata = new OgcCoverageServiceMetadata
        {
            Title = GetServiceTitle(capabilities),
            Description = Descendants(capabilities, "ServiceIdentification")
                .Select(static element => ChildValue(element, "Abstract"))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            Product = "OGC Web Coverage Service",
            ProviderName = Descendants(capabilities, "ProviderName")
                .Select(static element => element.Value.Trim())
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            Fees = ReadServiceConstraintValues(capabilities, "Fees"),
            AccessConstraints = ReadServiceConstraintValues(capabilities, "AccessConstraints")
        };
        var serviceFormats = ReadWcsServiceFormats(capabilities);
        var coverageIds = ReadWcsCoverageIds(capabilities);
        var coverages = new List<OgcCoverageDescription>(coverageIds.Length);

        foreach (var coverageId in coverageIds)
        {
            var description = await TryDescribeCoverageAsync(
                    serviceUri,
                    version,
                    coverageId,
                    serviceFormats,
                    request.TimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            coverages.Add(description);
        }

        var scanner = new OgcCoverageMigrationInventoryScanner(_crsRegistry);
        return await scanner.ScanAsync(
                new OgcCoverageServiceScanRequest
                {
                    ServiceType = "WCS",
                    ServiceUrl = serviceUri.ToString(),
                    Version = version,
                    ServiceMetadata = serviceMetadata,
                    Coverages = coverages.ToArray()
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MigrationSourceInventoryArtifact> BuildOgcApiCoveragesInventoryAsync(
        OgcServiceScanRequest request,
        Uri serviceUri,
        CancellationToken cancellationToken)
    {
        var collectionsUri = BuildOgcApiCoveragesCollectionsUrl(serviceUri);
        var payload = await _httpClient.GetStringAsync(collectionsUri, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var serviceMetadata = new OgcCoverageServiceMetadata
        {
            Title = ReadJsonString(root, "title") ?? serviceUri.Host,
            Description = ReadJsonString(root, "description"),
            Product = "OGC API Coverages"
        };

        var coverages = new List<OgcCoverageDescription>();
        if (root.TryGetProperty("collections", out var collections) &&
            collections.ValueKind == JsonValueKind.Array)
        {
            foreach (var collection in collections.EnumerateArray())
            {
                var id = ReadJsonString(collection, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                coverages.Add(new OgcCoverageDescription
                {
                    CoverageId = id,
                    Title = ReadJsonString(collection, "title"),
                    Description = ReadJsonString(collection, "description"),
                    CoverageType = ReadJsonString(collection, "itemType") ?? "Coverage",
                    Crs = ReadOgcApiCoverageCrs(collection),
                    Axes = ReadOgcApiCoverageAxes(collection),
                    OutputFormats = ReadOgcApiCoverageFormats(collection)
                });
            }
        }

        var scanner = new OgcCoverageMigrationInventoryScanner(_crsRegistry);
        return await scanner.ScanAsync(
                new OgcCoverageServiceScanRequest
                {
                    ServiceType = "OGC API Coverages",
                    ServiceUrl = serviceUri.ToString(),
                    Version = request.Version,
                    ServiceMetadata = serviceMetadata,
                    Coverages = coverages.ToArray()
                },
                cancellationToken)
            .ConfigureAwait(false);
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
        var dependencies = BuildWmsOperationDependencies(
            containerId,
            capabilities,
            version,
            ImportCompatibilityCodes.OgcWmsRenderOnlySource);

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
            ImportCompatibilityCodes.OgcWmsRenderOnlySource,
            dependencies);
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
            .Concat(BuildWmtsOperationDependencies(
                containerId,
                capabilities,
                version,
                ImportCompatibilityCodes.OgcWmtsTileOnlySource))
            .Concat(BuildWmtsResourceUrlDependencies(
                containerId,
                layers,
                ImportCompatibilityCodes.OgcWmtsTileOnlySource))
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
            ExternalDependencies = dependencies.OrderBy(static dependency => dependency.Id, StringComparer.Ordinal).ToArray(),
            FidelityClassifications = BuildFidelityClassifications(
                sourceKind,
                serviceType,
                resources,
                styles,
                dependencies)
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

    private async Task<OgcCoverageDescription> TryDescribeCoverageAsync(
        Uri serviceUri,
        string version,
        string coverageId,
        OgcCoverageFormatMetadata[] serviceFormats,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildDescribeCoverageUrl(serviceUri, version, coverageId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 60 : timeoutSeconds));
            var coverageXml = await _httpClient.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            var document = XDocument.Parse(coverageXml, LoadOptions.None);
            return BuildCoverageDescription(coverageId, document, serviceFormats);
        }
        catch (Exception ex) when (IsRecoverableDescribeCoverageException(ex))
        {
            return new OgcCoverageDescription
            {
                CoverageId = coverageId,
                OutputFormats = []
            };
        }
    }

    private static OgcCoverageDescription BuildCoverageDescription(
        string fallbackCoverageId,
        XDocument document,
        OgcCoverageFormatMetadata[] serviceFormats)
    {
        var coverageElement = Descendants(document, "CoverageDescription").FirstOrDefault() ??
                              Descendants(document, "CoverageOffering").FirstOrDefault() ??
                              document.Root;
        var coverageId = coverageElement == null
            ? fallbackCoverageId
            : FirstNonBlank(
                ChildValue(coverageElement, "CoverageId"),
                ChildValue(coverageElement, "Identifier"),
                ChildValue(coverageElement, "name"),
                fallbackCoverageId)!;
        var axes = coverageElement == null ? Array.Empty<OgcCoverageAxisMetadata>() : ReadWcsAxes(coverageElement);
        var ranges = coverageElement == null ? Array.Empty<OgcCoverageRangeMetadata>() : ReadWcsRanges(coverageElement);
        var formats = coverageElement == null ? serviceFormats : ReadWcsCoverageFormats(coverageElement, serviceFormats);
        var crs = coverageElement == null ? Array.Empty<OgcCoverageCrsMetadata>() : ReadWcsCoverageCrs(coverageElement);

        return new OgcCoverageDescription
        {
            CoverageId = coverageId,
            Title = coverageElement == null ? null : ChildValue(coverageElement, "Title"),
            Description = coverageElement == null ? null : ChildValue(coverageElement, "Abstract") ?? ChildValue(coverageElement, "description"),
            CoverageType = coverageElement?.Name.LocalName,
            NativeFormat = formats.FirstOrDefault(static format => format.IsNative == true)?.Format,
            Crs = crs,
            Axes = axes,
            Ranges = ranges,
            OutputFormats = formats
        };
    }

    private static OgcCoverageCrsMetadata[] ReadWcsCoverageCrs(XElement coverageElement)
    {
        var values = new List<OgcCoverageCrsMetadata>();
        var envelope = Descendants(coverageElement, "Envelope").FirstOrDefault();
        var envelopeCrs = envelope?.Attribute("srsName")?.Value;
        if (!string.IsNullOrWhiteSpace(envelopeCrs))
        {
            values.Add(new OgcCoverageCrsMetadata
            {
                Role = "native",
                Crs = envelopeCrs.Trim(),
                AxisLabels = SplitTokens(envelope?.Attribute("axisLabels")?.Value)
            });
        }

        foreach (var crsElement in Descendants(coverageElement, "SupportedCRS")
                     .Concat(Descendants(coverageElement, "supportedCRS"))
                     .Concat(Descendants(coverageElement, "nativeCRS")))
        {
            var value = crsElement.Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(new OgcCoverageCrsMetadata
                {
                    Role = crsElement.Name.LocalName.Contains("native", StringComparison.OrdinalIgnoreCase) ? "native" : "supported",
                    Crs = value
                });
            }
        }

        return values
            .GroupBy(static item => $"{item.Role}\u001f{item.Crs}", StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static item => item.Role, StringComparer.Ordinal)
            .ThenBy(static item => item.Crs, StringComparer.Ordinal)
            .ToArray();
    }

    private static OgcCoverageAxisMetadata[] ReadWcsAxes(XElement coverageElement)
    {
        var axisLabels = Descendants(coverageElement, "axisLabels")
            .SelectMany(static element => SplitTokens(element.Value))
            .Concat(Descendants(coverageElement, "Envelope")
                .SelectMany(static element => SplitTokens(element.Attribute("axisLabels")?.Value)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var lower = Descendants(coverageElement, "lowerCorner")
            .SelectMany(static element => SplitTokens(element.Value))
            .ToArray();
        var upper = Descendants(coverageElement, "upperCorner")
            .SelectMany(static element => SplitTokens(element.Value))
            .ToArray();

        return axisLabels
            .Select((axis, index) => new OgcCoverageAxisMetadata
            {
                Name = axis,
                AxisType = NormalizeAxisType(axis),
                LowerBound = index < lower.Length ? lower[index] : null,
                UpperBound = index < upper.Length ? upper[index] : null,
                Subsettable = true
            })
            .OrderBy(static axis => axis.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static OgcCoverageRangeMetadata[] ReadWcsRanges(XElement coverageElement)
        => Descendants(coverageElement, "field")
            .Select(field => new OgcCoverageRangeMetadata
            {
                Name = field.Attribute("name")?.Value ?? ChildValue(field, "name") ?? "range",
                Label = ChildValue(field, "label"),
                DataType = Descendants(field, "DataType").Select(static item => item.Value.Trim()).FirstOrDefault(static value => value.Length > 0) ??
                           Descendants(field, "dataType").Select(static item => item.Value.Trim()).FirstOrDefault(static value => value.Length > 0),
                Unit = Descendants(field, "uom").Select(static item => item.Attribute("code")?.Value ?? item.Value.Trim()).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                NoDataValue = Descendants(field, "nilValue").Select(static item => item.Value.Trim()).FirstOrDefault(static value => value.Length > 0),
                Interpretation = ChildValue(field, "definition")
            })
            .OrderBy(static range => range.Name, StringComparer.Ordinal)
            .ToArray();

    private static OgcCoverageFormatMetadata[] ReadWcsCoverageFormats(
        XElement coverageElement,
        OgcCoverageFormatMetadata[] serviceFormats)
    {
        var formats = Descendants(coverageElement, "format")
            .Concat(Descendants(coverageElement, "SupportedFormat"))
            .Select(element => BuildCoverageFormat(element.Value, isNative: null))
            .Concat(serviceFormats)
            .Where(static format => !string.IsNullOrWhiteSpace(format.Format))
            .GroupBy(static format => NormalizeFormatKey(format.Format), StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static format => format.Format, StringComparer.Ordinal)
            .ToArray();
        return formats.Length == 0 ? serviceFormats : formats;
    }

    private static bool IsRecoverableDescribeCoverageException(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or System.Xml.XmlException;

    private static string ToSafeScanFailureReason(Exception exception)
        => exception switch
        {
            TaskCanceledException => "OGC service scan timed out.",
            HttpRequestException => "OGC service endpoint could not be reached or returned an unsupported response.",
            System.Xml.XmlException => "OGC service capabilities metadata could not be parsed.",
            JsonException => "OGC API Coverages metadata could not be parsed.",
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
                    Metadata = BuildOptionalMetadata(("title", ChildValue(style, "Title"))),
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
                    Metadata = BuildOptionalMetadata(("title", ChildValue(style, "Title"))),
                    Compatibility = MigrationInventoryHelpers.Partial(
                        "WMTS style metadata was captured for manual tile-service migration planning.",
                        [],
                        ["Review WMTS style semantics and recreate equivalent Honua styles where required."],
                        ImportCompatibilityCodes.OgcWmtsTileOnlySource)
                };
            })
            .ToArray();
    }

    private static MigrationExternalDependency[] BuildWmsOperationDependencies(
        string containerId,
        XDocument capabilities,
        string version,
        string compatibilityCode)
    {
        var dependencies = new List<MigrationExternalDependency>();
        AddWmsOperationDependency(
            dependencies,
            containerId,
            capabilities,
            version,
            operationName: "GetMap",
            dependencyType: "render",
            compatibilityCode);
        AddWmsOperationDependency(
            dependencies,
            containerId,
            capabilities,
            version,
            operationName: "GetFeatureInfo",
            dependencyType: "feature-info",
            compatibilityCode);

        return dependencies.ToArray();
    }

    private static void AddWmsOperationDependency(
        List<MigrationExternalDependency> dependencies,
        string containerId,
        XDocument capabilities,
        string version,
        string operationName,
        string dependencyType,
        string compatibilityCode)
    {
        var operation = Descendants(capabilities, operationName)
            .FirstOrDefault(static element => element.Parent?.Name.LocalName == "Request");
        if (operation == null)
        {
            return;
        }

        var formats = Elements(operation, "Format")
            .Select(static element => element.Value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        dependencies.Add(new MigrationExternalDependency
        {
            Id = $"endpoint:wms:{ToOperationId(operationName)}",
            ContainerId = containerId,
            Kind = "ogc-endpoint",
            Name = $"WMS {operationName}",
            DependencyType = dependencyType,
            Address = ToSafeExternalAddress(ReadOnlineResourceHref(operation)),
            Metadata = BuildOperationMetadata("WMS", version, operationName, formats),
            Compatibility = MigrationInventoryHelpers.Partial(
                $"WMS {operationName} endpoint metadata was captured for manual render-service migration planning.",
                [],
                ["Review equivalent Honua map-service routing, supported formats, and client cutover URLs."],
                compatibilityCode)
        });
    }

    private static MigrationExternalDependency[] BuildWmtsOperationDependencies(
        string containerId,
        XDocument capabilities,
        string version,
        string compatibilityCode)
    {
        var dependencies = new List<MigrationExternalDependency>();
        AddWmtsOperationDependency(
            dependencies,
            containerId,
            capabilities,
            version,
            operationName: "GetTile",
            dependencyType: "tile",
            compatibilityCode);
        AddWmtsOperationDependency(
            dependencies,
            containerId,
            capabilities,
            version,
            operationName: "GetFeatureInfo",
            dependencyType: "feature-info",
            compatibilityCode);

        return dependencies.ToArray();
    }

    private static void AddWmtsOperationDependency(
        List<MigrationExternalDependency> dependencies,
        string containerId,
        XDocument capabilities,
        string version,
        string operationName,
        string dependencyType,
        string compatibilityCode)
    {
        var operation = Descendants(capabilities, "Operation")
            .FirstOrDefault(element => string.Equals(
                element.Attribute("name")?.Value,
                operationName,
                StringComparison.OrdinalIgnoreCase));
        if (operation == null)
        {
            return;
        }

        dependencies.Add(new MigrationExternalDependency
        {
            Id = $"endpoint:wmts:{ToOperationId(operationName)}",
            ContainerId = containerId,
            Kind = "ogc-endpoint",
            Name = $"WMTS {operationName}",
            DependencyType = dependencyType,
            Address = ToSafeExternalAddress(ReadOnlineResourceHref(operation)),
            Metadata = BuildOperationMetadata("WMTS", version, operationName, []),
            Compatibility = MigrationInventoryHelpers.Partial(
                $"WMTS {operationName} endpoint metadata was captured for manual tile-service migration planning.",
                [],
                ["Review equivalent Honua tile-service routing, cache grid configuration, and client cutover URLs."],
                compatibilityCode)
        });
    }

    private static MigrationExternalDependency[] BuildWmtsResourceUrlDependencies(
        string containerId,
        IReadOnlyList<XElement> layers,
        string compatibilityCode)
    {
        return layers
            .SelectMany(layer =>
            {
                var layerId = ChildValue(layer, "Identifier");
                if (string.IsNullOrWhiteSpace(layerId))
                {
                    return [];
                }

                var resourceId = $"wmts-layer:{ToStableId(layerId)}";
                return Elements(layer, "ResourceURL")
                    .Select(resourceUrl =>
                    {
                        var resourceType = AttributeValue(resourceUrl, "resourceType") ?? "resource";
                        var format = AttributeValue(resourceUrl, "format");
                        var template = AttributeValue(resourceUrl, "template");
                        return new MigrationExternalDependency
                        {
                            Id = $"endpoint:wmts:{ToStableId(resourceType)}:{ToStableId(layerId)}:{ToStableId(format ?? "default")}",
                            ContainerId = containerId,
                            ResourceId = resourceId,
                            Kind = "ogc-endpoint",
                            Name = $"WMTS {resourceType} template {layerId}",
                            DependencyType = $"resource-url:{resourceType}",
                            Address = ToSafeExternalAddress(template),
                            Metadata = BuildOptionalMetadata(
                                ("service", "WMTS"),
                                ("operation", "ResourceURL"),
                                ("resourceType", resourceType),
                                ("format", format)),
                            Compatibility = MigrationInventoryHelpers.Partial(
                                "WMTS ResourceURL template was captured for manual tile-service migration planning.",
                                [],
                                ["Review tile template compatibility with Honua cache and tile matrix configuration."],
                                compatibilityCode)
                        };
                    });
            })
            .OrderBy(static dependency => dependency.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static MigrationFidelityClassificationRecord[] BuildFidelityClassifications(
        string sourceKind,
        string serviceType,
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[] styles,
        MigrationExternalDependency[] dependencies)
    {
        var records = new List<MigrationFidelityClassificationRecord>();

        foreach (var resource in resources.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            records.Add(CreateFidelityRecord(
                sourceKind,
                serviceType,
                resource.Id,
                resource.Kind,
                GetResourceFidelityCategory(sourceKind, resource),
                resource.Name,
                GetAutomationStatus(resource.Compatibility),
                resource.Compatibility,
                GetResourceTargetKind(sourceKind, resource),
                resource.StyleIds.Concat(resource.ExternalDependencyIds)));
        }

        foreach (var style in styles.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            records.Add(CreateFidelityRecord(
                sourceKind,
                serviceType,
                style.Id,
                style.Kind,
                "style",
                style.Name,
                GetAutomationStatus(style.Compatibility),
                style.Compatibility,
                "style",
                style.ResourceIds.Concat(style.ExternalDependencyIds)));
        }

        foreach (var dependency in dependencies.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            records.Add(CreateFidelityRecord(
                sourceKind,
                serviceType,
                dependency.Id,
                dependency.Kind,
                GetDependencyFidelityCategory(dependency),
                dependency.Name,
                GetAutomationStatus(dependency.Compatibility),
                dependency.Compatibility,
                dependency.Kind,
                string.IsNullOrWhiteSpace(dependency.ResourceId) ? [] : [dependency.ResourceId]));
        }

        return records
            .OrderBy(static record => record.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static MigrationFidelityClassificationRecord CreateFidelityRecord(
        string sourceKind,
        string serviceType,
        string sourceId,
        string kind,
        string category,
        string? name,
        string automationStatus,
        MigrationCompatibilityAssessment compatibility,
        string? targetKind,
        IEnumerable<string> relatedIds)
        => new()
        {
            Id = $"classification:{sourceId}:{category}",
            SourceId = sourceId,
            Kind = kind,
            Category = category,
            Name = name,
            AutomationStatus = automationStatus,
            Code = compatibility.Code ?? BuildFallbackFidelityCode(sourceKind, kind, category),
            Reason = compatibility.Reason,
            TargetKind = targetKind,
            ManualSteps = automationStatus == MigrationFidelityAutomationStatuses.Automated
                ? []
                : MigrationInventoryHelpers.NormalizeStrings(compatibility.ManualSteps),
            RelatedIds = MigrationInventoryHelpers.NormalizeStrings(relatedIds),
            Metadata = BuildOptionalMetadata(
                ("sourceKind", sourceKind),
                ("serviceType", serviceType),
                ("compatibilityLevel", compatibility.Level))
        };

    private static string GetResourceFidelityCategory(string sourceKind, MigrationInventoryResource resource)
    {
        if (sourceKind.Equals("ogc-wfs", StringComparison.OrdinalIgnoreCase))
        {
            return "feature-schema";
        }

        return sourceKind.Equals("ogc-wmts", StringComparison.OrdinalIgnoreCase)
            ? "tile-data-copy"
            : "render-data-copy";
    }

    private static string? GetResourceTargetKind(string sourceKind, MigrationInventoryResource resource)
    {
        if (sourceKind.Equals("ogc-wfs", StringComparison.OrdinalIgnoreCase) &&
            resource.Kind.Equals("feature-type", StringComparison.OrdinalIgnoreCase))
        {
            return "feature-layer";
        }

        if (sourceKind.Equals("ogc-wmts", StringComparison.OrdinalIgnoreCase))
        {
            return "tile-service-layer";
        }

        if (sourceKind.Equals("ogc-wms", StringComparison.OrdinalIgnoreCase))
        {
            return "map-service-layer";
        }

        return null;
    }

    private static string GetDependencyFidelityCategory(MigrationExternalDependency dependency)
        => dependency.Kind.Equals("tile-matrix-set", StringComparison.OrdinalIgnoreCase)
            ? "tile-matrix-set"
            : "service-endpoint";

    private static string GetAutomationStatus(MigrationCompatibilityAssessment compatibility)
    {
        if (IsCompatible(compatibility.Level))
        {
            return MigrationFidelityAutomationStatuses.Automated;
        }

        return IsIncompatible(compatibility.Level)
            ? MigrationFidelityAutomationStatuses.Unsupported
            : MigrationFidelityAutomationStatuses.ManualReview;
    }

    private static bool IsCompatible(string? level)
        => string.Equals(level, "compatible", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncompatible(string? level)
        => string.Equals(level, "incompatible", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> BuildOperationMetadata(
        string serviceType,
        string version,
        string operationName,
        string[] formats)
    {
        var metadata = BuildOptionalMetadata(
            ("service", serviceType),
            ("version", version),
            ("operation", operationName));
        if (formats.Length > 0)
        {
            metadata["formats"] = string.Join(",", formats);
        }

        return metadata;
    }

    private static Dictionary<string, string> BuildOptionalMetadata(params (string Key, string? Value)[] pairs)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value.Trim();
            }
        }

        return metadata;
    }

    private static string? ReadOnlineResourceHref(XElement element)
        => Descendants(element, "OnlineResource")
            .Concat(Descendants(element, "Get"))
            .Select(static candidate => AttributeValue(candidate, "href"))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? ToSafeExternalAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var trimmed = address.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var fragmentIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
            if (fragmentIndex >= 0)
            {
                trimmed = trimmed[..fragmentIndex];
            }

            var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex < 0)
            {
                return trimmed;
            }

            var safeQuery = BuildSafeCapabilitiesQuery(trimmed[queryIndex..]);
            return safeQuery.Length == 0
                ? trimmed[..queryIndex]
                : $"{trimmed[..queryIndex]}?{safeQuery}";
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Fragment = string.Empty,
            Query = BuildSafeCapabilitiesQuery(uri.Query)
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string? AttributeValue(XElement element, string localName)
        => element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))?
            .Value
            .Trim();

    private static string ToOperationId(string operationName)
        => operationName switch
        {
            "GetCapabilities" => "get-capabilities",
            "GetFeatureInfo" => "get-feature-info",
            "GetMap" => "get-map",
            "GetTile" => "get-tile",
            _ => ToStableId(operationName)
        };

    private static string BuildFallbackFidelityCode(string sourceKind, string kind, string category)
        => $"{ToCodePart(sourceKind)}_{ToCodePart(kind)}_{ToCodePart(category)}";

    private static string ToCodePart(string value)
        => ToStableId(value)
            .Replace('-', '_')
            .ToUpperInvariant();

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

    private static string[] ReadWcsCoverageIds(XDocument capabilities)
        => Descendants(capabilities, "CoverageSummary")
            .Select(static element => ChildValue(element, "CoverageId") ?? ChildValue(element, "Identifier") ?? ChildValue(element, "Name"))
            .Concat(Descendants(capabilities, "CoverageOfferingBrief").Select(static element => ChildValue(element, "name")))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static OgcCoverageFormatMetadata[] ReadWcsServiceFormats(XDocument capabilities)
        => Descendants(capabilities, "formatSupported")
            .Concat(Descendants(capabilities, "FormatSupported"))
            .Concat(Descendants(capabilities, "formats"))
            .Select(static element => BuildCoverageFormat(element.Value, isNative: null))
            .Where(static format => !string.IsNullOrWhiteSpace(format.Format))
            .GroupBy(static format => NormalizeFormatKey(format.Format), StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static format => format.Format, StringComparer.Ordinal)
            .ToArray();

    private static string[] ReadServiceConstraintValues(XDocument document, string name)
        => Descendants(document, name)
            .Select(static element => element.Value.Trim())
            .Concat(Descendants(document, "Fees").Where(_ => string.Equals(name, "Fees", StringComparison.Ordinal)).Select(static element => element.Value.Trim()))
            .Where(static value => !string.IsNullOrWhiteSpace(value) &&
                                   !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static Uri BuildDescribeCoverageUrl(Uri serviceUri, string version, string coverageId)
    {
        var isWcs2 = version.StartsWith("2.", StringComparison.Ordinal);
        var builder = new UriBuilder(serviceUri)
        {
            Query = BuildQuery(
                serviceUri.Query,
                new Dictionary<string, string?>
                {
                    ["service"] = "WCS",
                    ["version"] = version.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : version,
                    ["request"] = "DescribeCoverage",
                    [isWcs2 ? "coverageId" : "coverage"] = coverageId
                })
        };
        return builder.Uri;
    }

    private static Uri BuildOgcApiCoveragesCollectionsUrl(Uri serviceUri)
    {
        if (serviceUri.AbsolutePath.EndsWith("/collections", StringComparison.OrdinalIgnoreCase))
        {
            return serviceUri;
        }

        var builder = new UriBuilder(serviceUri);
        builder.Path = $"{builder.Path.TrimEnd('/')}/collections";
        return builder.Uri;
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static OgcCoverageCrsMetadata[] ReadOgcApiCoverageCrs(JsonElement collection)
    {
        var values = new List<OgcCoverageCrsMetadata>();
        if (collection.TryGetProperty("crs", out var crs) && crs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in crs.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(new OgcCoverageCrsMetadata { Role = "supported", Crs = value });
                }
            }
        }

        var extentCrs = TryReadNestedString(collection, "extent", "spatial", "crs");
        if (!string.IsNullOrWhiteSpace(extentCrs))
        {
            values.Add(new OgcCoverageCrsMetadata { Role = "native", Crs = extentCrs });
        }

        return values
            .GroupBy(static value => $"{value.Role}\u001f{value.Crs}", StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static value => value.Role, StringComparer.Ordinal)
            .ThenBy(static value => value.Crs, StringComparer.Ordinal)
            .ToArray();
    }

    private static OgcCoverageAxisMetadata[] ReadOgcApiCoverageAxes(JsonElement collection)
    {
        if (!collection.TryGetProperty("extent", out var extent) ||
            !extent.TryGetProperty("spatial", out var spatial) ||
            !spatial.TryGetProperty("bbox", out var bbox) ||
            bbox.ValueKind != JsonValueKind.Array ||
            bbox.GetArrayLength() == 0)
        {
            return [];
        }

        var firstBbox = bbox[0];
        if (firstBbox.ValueKind != JsonValueKind.Array || firstBbox.GetArrayLength() < 4)
        {
            return [];
        }

        return
        [
            new OgcCoverageAxisMetadata
            {
                Name = "x",
                AxisType = "x",
                LowerBound = firstBbox[0].ToString(),
                UpperBound = firstBbox[2].ToString(),
                Subsettable = true
            },
            new OgcCoverageAxisMetadata
            {
                Name = "y",
                AxisType = "y",
                LowerBound = firstBbox[1].ToString(),
                UpperBound = firstBbox[3].ToString(),
                Subsettable = true
            }
        ];
    }

    private static OgcCoverageFormatMetadata[] ReadOgcApiCoverageFormats(JsonElement collection)
    {
        if (!collection.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return links.EnumerateArray()
            .Where(static link => IsOgcApiCoverageOutputLink(link))
            .Select(link => ReadJsonString(link, "type"))
            .Where(static type => !string.IsNullOrWhiteSpace(type))
            .Select(static type => BuildCoverageFormat(type!, isNative: null))
            .GroupBy(static format => NormalizeFormatKey(format.Format), StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static format => format.Format, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsOgcApiCoverageOutputLink(JsonElement link)
    {
        var rel = ReadJsonString(link, "rel");
        if (string.IsNullOrWhiteSpace(rel))
        {
            return false;
        }

        return rel.Equals("coverage", StringComparison.OrdinalIgnoreCase) ||
               rel.EndsWith("/coverage", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static OgcCoverageFormatMetadata BuildCoverageFormat(string format, bool? isNative)
        => new()
        {
            Format = format.Trim(),
            MediaType = LooksLikeMediaType(format) ? format.Trim() : null,
            Profile = IsCogFormat(format) ? "cloud-optimized-geotiff" : null,
            IsNative = isNative
        };

    private static string NormalizeFormatKey(string value)
        => value.Trim().ToUpperInvariant();

    private static bool LooksLikeMediaType(string value)
        => value.Contains('/', StringComparison.Ordinal);

    private static bool IsCogFormat(string value)
        => value.Contains("cog", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("cloud-optimized", StringComparison.OrdinalIgnoreCase);

    private static string[] SplitTokens(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeAxisType(string axis)
    {
        var normalized = axis.Trim().ToLowerInvariant();
        return normalized switch
        {
            "long" or "lon" or "longitude" or "x" => "x",
            "lat" or "latitude" or "y" => "y",
            "time" or "ansi" => "time",
            "elev" or "elevation" or "height" => "elevation",
            _ => normalized
        };
    }

    private static string NormalizeServiceType(string serviceType)
    {
        var normalized = serviceType.Trim().ToUpperInvariant();
        return normalized switch
        {
            "WFS" or "WMS" or "WMTS" or "WCS" => normalized,
            "OGC API COVERAGES" or "OGC-API-COVERAGES" or "OGC_API_COVERAGES" or "COVERAGES" => "OGC API Coverages",
            _ => throw new InvalidOperationException("Unsupported OGC service type.")
        };
    }

    private static string GetSourceKind(string normalizedServiceType)
        => normalizedServiceType switch
        {
            "OGC API Coverages" => "ogc-api-coverages",
            _ => $"ogc-{normalizedServiceType.ToLowerInvariant()}"
        };

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
        if (string.Equals(serviceType, "OGC API Coverages", StringComparison.Ordinal))
        {
            return serviceUri;
        }

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
    {
        var normalized = key
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("signature", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("passwd", StringComparison.Ordinal) ||
               normalized.Equals("auth", StringComparison.Ordinal) ||
               normalized.Equals("authorization", StringComparison.Ordinal) ||
               normalized.Equals("key", StringComparison.Ordinal) ||
               normalized.Equals("pwd", StringComparison.Ordinal) ||
               normalized.Equals("session", StringComparison.Ordinal) ||
               normalized.Equals("sig", StringComparison.Ordinal);
    }

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
