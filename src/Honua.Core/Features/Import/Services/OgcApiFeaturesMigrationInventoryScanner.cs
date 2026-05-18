// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Builds migration source inventory artifacts from captured OGC API Features scan facts.
/// </summary>
public static class OgcApiFeaturesMigrationInventoryScanner
{
    private const string SourceKind = "ogc-api-features";
    private const string ContainerId = "service:ogc-api-features";

    private static readonly string[] KnownCollectionLinkRelations =
    [
        "self",
        "alternate",
        "items",
        "item",
        "collection",
        "queryables",
        "describedby",
        "schema",
        "service-desc",
        "service-doc",
        "conformance",
        "data",
        "next",
        "prev",
        "previous",
        "http://www.opengis.net/def/rel/ogc/1.0/queryables",
        "http://www.opengis.net/def/rel/ogc/1.0/schema"
    ];

    /// <summary>
    /// Builds a deterministic migration source inventory artifact from captured OGC API Features scan facts.
    /// </summary>
    /// <param name="snapshot">Captured OGC API Features source snapshot.</param>
    /// <returns>Shared migration source inventory artifact.</returns>
    public static MigrationSourceInventoryArtifact BuildInventory(OgcApiFeaturesMigrationSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var baseUri = CreateBaseUri(snapshot.BaseUrl);
        var displayName = string.IsNullOrWhiteSpace(snapshot.Title) ? baseUri.Host : snapshot.Title.Trim();
        var dependencies = new List<MigrationExternalDependency>();

        dependencies.AddRange(BuildLandingPageDependencies(snapshot, baseUri));
        dependencies.AddRange(BuildConformanceDependencies(snapshot.ConformanceClasses));
        dependencies.AddRange(BuildVendorExtensionDependencies(snapshot.VendorExtensions, resourceId: null));

        var collections = snapshot.Collections
            .Where(static collection => !string.IsNullOrWhiteSpace(collection.Id))
            .OrderBy(static collection => collection.Id, StringComparer.Ordinal)
            .ToArray();

        var resources = new MigrationInventoryResource[collections.Length];
        var hasTransactionalConformance = HasTransactionalConformance(snapshot.ConformanceClasses);
        var sourceCrsDeclarations = snapshot.CrsDeclarations;

        for (var index = 0; index < collections.Length; index++)
        {
            var collection = collections[index];
            var resourceId = $"collection:{ToStableId(collection.Id)}";
            dependencies.AddRange(BuildCollectionDependencies(collection, resourceId, baseUri));
            dependencies.AddRange(BuildVendorExtensionDependencies(collection.VendorExtensions, resourceId));

            resources[index] = BuildResource(
                collection,
                sourceCrsDeclarations,
                resourceId,
                baseUri,
                hasTransactionalConformance);
        }

        var orderedDependencies = MigrationInventoryHelpers.OrderByStableKey(dependencies, static dependency => dependency.Id);
        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = ContainerId,
                Kind = "ogc-api-features-service",
                Name = "OGC API Features",
                Title = displayName,
                Description = snapshot.Description,
                IsDefault = true,
                Compatibility = BuildContainerCompatibility(resources, orderedDependencies)
            }
        };

        var orderedResources = MigrationInventoryHelpers.OrderByStableKey(resources, static resource => resource.Id);
        var overallCompatibility = MigrationInventoryHelpers.Aggregate(
            containers.Select(static container => container.Compatibility)
                .Concat(orderedResources.Select(static resource => resource.Compatibility))
                .Concat(orderedDependencies.Select(static dependency => dependency.Compatibility)),
            "No OGC API Features collections were discovered.");

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = SourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = displayName,
                BaseUrl = NormalizeAddress(baseUri.ToString(), baseUri) ?? baseUri.ToString(),
                Product = "OGC API Features",
                Version = string.IsNullOrWhiteSpace(snapshot.Version) ? null : snapshot.Version.Trim(),
                ServiceType = "OGC API Features"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "not-captured",
                CredentialsSupplied = false,
                AccessConfirmed = true,
                Notes = ["Authentication posture is outside this core inventory-planning slice."]
            },
            ScanCompleteness = BuildCompleteness(resources, orderedDependencies),
            Summary = MigrationInventoryHelpers.BuildSummary(containers, orderedResources, [], orderedDependencies),
            OverallCompatibility = overallCompatibility,
            Containers = containers,
            Resources = orderedResources,
            ExternalDependencies = orderedDependencies
        };
    }

    private static MigrationInventoryResource BuildResource(
        OgcApiFeaturesCollectionSnapshot collection,
        OgcApiFeaturesCrsDeclaration[] sourceCrsDeclarations,
        string resourceId,
        Uri baseUri,
        bool hasTransactionalConformance)
    {
        var itemLinks = GetItemLinks(collection).ToArray();
        var queryablesLinks = GetQueryablesLinks(collection.Links).ToArray();
        var schemaLinks = GetSchemaLinks(collection.Links).ToArray();
        var spatialReferences = BuildSpatialReferences(
            collection.CrsDeclarations.Length > 0 ? collection.CrsDeclarations : sourceCrsDeclarations);
        var itemEncodings = BuildItemEncodings(collection, itemLinks, collection.PaginationLinks);

        return new MigrationInventoryResource
        {
            Id = resourceId,
            ContainerId = ContainerId,
            Kind = "ogc-api-features-collection",
            Name = collection.Id.Trim(),
            Title = string.IsNullOrWhiteSpace(collection.Title) ? null : collection.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(collection.Description) ? null : collection.Description.Trim(),
            GeometryType = string.IsNullOrWhiteSpace(collection.GeometryType) ? null : collection.GeometryType.Trim(),
            FeatureCount = collection.FeatureCount,
            Capabilities = BuildCollectionCapabilities(
                itemLinks,
                itemEncodings,
                queryablesLinks,
                schemaLinks,
                spatialReferences,
                collection.PaginationLinks,
                hasTransactionalConformance,
                collection.VendorExtensions),
            SpatialReferences = spatialReferences,
            ExternalDependencyIds = BuildRelatedDependencyIds(collection, resourceId, baseUri),
            Compatibility = AssessCollectionCompatibility(
                collection,
                itemLinks,
                queryablesLinks,
                schemaLinks,
                spatialReferences,
                itemEncodings,
                hasTransactionalConformance)
        };
    }

    private static MigrationCompatibilityAssessment AssessCollectionCompatibility(
        OgcApiFeaturesCollectionSnapshot collection,
        OgcApiFeaturesLink[] itemLinks,
        OgcApiFeaturesLink[] queryablesLinks,
        OgcApiFeaturesLink[] schemaLinks,
        MigrationSpatialReferenceInfo[] spatialReferences,
        string[] itemEncodings,
        bool hasTransactionalConformance)
    {
        if (itemLinks.Length == 0 && collection.PaginationLinks.Length == 0)
        {
            return MigrationInventoryHelpers.Incompatible(
                "OGC API Features collection does not advertise an items endpoint.",
                manualSteps: ["Confirm the collection exposes /items or exclude it from automated feature import."],
                code: OgcApiFeaturesImportCompatibilityCodes.MissingItemsEndpoint);
        }

        if (!itemEncodings.Any(IsJsonEncoding))
        {
            return MigrationInventoryHelpers.Incompatible(
                "OGC API Features collection items are not advertised with a JSON representation.",
                warnings: itemEncodings.Length == 0 ? ["No item representation media types were captured."] : itemEncodings,
                manualSteps: ["Expose GeoJSON FeatureCollection items or map the source through a custom import adapter."],
                code: OgcApiFeaturesImportCompatibilityCodes.NonJsonItemsEncoding);
        }

        var warnings = new List<string>();
        var manualSteps = new List<string>();

        if (queryablesLinks.Length == 0)
        {
            warnings.Add("Collection does not advertise a queryables link.");
            manualSteps.Add("Confirm filterable fields from source documentation before migration.");
        }

        if (schemaLinks.Length == 0)
        {
            warnings.Add("Collection does not advertise a schema or describedby link.");
            manualSteps.Add("Confirm field names, types, nullability, and geometry mapping before import.");
        }

        if (collection.PaginationLinks.Length == 0)
        {
            warnings.Add("Items page pagination links were not captured.");
            manualSteps.Add("Confirm source paging parameters and deterministic traversal before bulk import.");
        }

        var nonJsonEncodings = itemEncodings.Where(static encoding => !IsJsonEncoding(encoding) && !IsHtmlEncoding(encoding)).ToArray();
        if (nonJsonEncodings.Length > 0)
        {
            warnings.Add($"Collection also advertises non-JSON item representations: {string.Join(", ", nonJsonEncodings)}.");
            manualSteps.Add("Use the JSON representation for automated import and review non-JSON representations separately.");
        }

        var unusualCrs = spatialReferences
            .Where(static spatialReference => !IsPortableDefaultCrs(spatialReference))
            .Select(static spatialReference => spatialReference.SourceValue ?? spatialReference.CrsUri)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (unusualCrs.Length > 0)
        {
            warnings.Add($"Collection advertises CRS declarations requiring transformation review: {string.Join(", ", unusualCrs)}.");
            manualSteps.Add("Confirm source CRS axis order and target CRS transformation behavior before cutover.");
        }

        var unknownRelations = GetUnknownLinkRelations(collection.Links).ToArray();
        if (unknownRelations.Length > 0)
        {
            warnings.Add($"Collection advertises link relations requiring manual review: {string.Join(", ", unknownRelations)}.");
            manualSteps.Add("Classify non-standard link relations before relying on the inventory for cutover.");
        }

        var vendorExtensions = MigrationInventoryHelpers.NormalizeStrings(collection.VendorExtensions);
        if (vendorExtensions.Length > 0)
        {
            warnings.Add($"Collection advertises vendor extensions: {string.Join(", ", vendorExtensions)}.");
            manualSteps.Add("Classify vendor extensions as automated, assisted, manual-review, or unsupported before cutover.");
        }

        if (hasTransactionalConformance)
        {
            warnings.Add("Source advertises OGC API Features transaction conformance.");
            manualSteps.Add("Confirm source-side transactions are not required during one-way migration cutover.");
        }

        return warnings.Count == 0
            ? MigrationInventoryHelpers.Compatible(
                "OGC API Features collection advertises GeoJSON items, schema/queryables links, CRS metadata, and pagination links.",
                code: OgcApiFeaturesImportCompatibilityCodes.CollectionSource)
            : MigrationInventoryHelpers.Partial(
                "OGC API Features collection can be inventoried, but one or more source capabilities need manual review.",
                warnings,
                manualSteps,
                OgcApiFeaturesImportCompatibilityCodes.ManualReview);
    }

    private static IEnumerable<MigrationExternalDependency> BuildLandingPageDependencies(
        OgcApiFeaturesMigrationSourceSnapshot snapshot,
        Uri baseUri)
    {
        yield return new MigrationExternalDependency
        {
            Id = "endpoint:ogc-api-features:landing-page",
            ContainerId = ContainerId,
            Kind = "ogc-api-features-endpoint",
            Name = "Landing page",
            DependencyType = "landing-page",
            Address = NormalizeAddress(snapshot.BaseUrl, baseUri),
            Metadata = new Dictionary<string, string>
            {
                ["rel"] = "self"
            },
            Compatibility = MigrationInventoryHelpers.Compatible(
                "OGC API Features landing page was captured for migration planning.",
                code: OgcApiFeaturesImportCompatibilityCodes.CollectionSource)
        };

        foreach (var link in snapshot.LandingPageLinks.OrderBy(static link => link.Rel, StringComparer.Ordinal)
                     .ThenBy(static link => link.Href, StringComparer.Ordinal))
        {
            if (!IsLandingPagePlanningLink(link))
            {
                continue;
            }

            var address = NormalizeAddress(link.Href, baseUri);
            yield return new MigrationExternalDependency
            {
                Id = BuildDependencyId($"endpoint:ogc-api-features:{ToStableId(link.Rel)}", address ?? link.Href),
                ContainerId = ContainerId,
                Kind = "ogc-api-features-endpoint",
                Name = BuildLinkName(link, "Landing page link"),
                DependencyType = NormalizeRel(link.Rel),
                Address = address,
                Metadata = BuildLinkMetadata(link, baseUri),
                Compatibility = MigrationInventoryHelpers.Compatible(
                    "OGC API Features landing page link was captured for migration planning.",
                    code: OgcApiFeaturesImportCompatibilityCodes.CollectionSource)
            };
        }
    }

    private static IEnumerable<MigrationExternalDependency> BuildCollectionDependencies(
        OgcApiFeaturesCollectionSnapshot collection,
        string resourceId,
        Uri baseUri)
    {
        return collection.Links.Concat(collection.PaginationLinks)
            .Select(link => BuildCollectionDependency(link, resourceId, baseUri))
            .Where(static dependency => dependency != null)
            .Select(static dependency => dependency!)
            .OrderBy(static dependency => dependency.Id, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.Name, StringComparer.Ordinal)
            .ThenBy(static dependency => GetMetadataValue(dependency, "rel"), StringComparer.Ordinal)
            .ThenBy(static dependency => GetMetadataValue(dependency, "type"), StringComparer.Ordinal)
            .ThenBy(static dependency => GetMetadataValue(dependency, "title"), StringComparer.Ordinal)
            .ThenBy(static dependency => GetMetadataValue(dependency, "queryParameters"), StringComparer.Ordinal)
            .DistinctBy(static dependency => dependency.Id, StringComparer.Ordinal);
    }

    private static MigrationExternalDependency? BuildCollectionDependency(
        OgcApiFeaturesLink link,
        string resourceId,
        Uri baseUri)
    {
        var dependencyType = ClassifyCollectionLink(link);
        if (dependencyType == null)
        {
            return null;
        }

        var address = NormalizeAddress(link.Href, baseUri);
        var compatibility = string.Equals(dependencyType, "manual-review-link", StringComparison.Ordinal)
            ? MigrationInventoryHelpers.Partial(
                "OGC API Features link relation needs manual migration review.",
                manualSteps: ["Classify the link relation as automated, assisted, manual-review, or unsupported."],
                code: OgcApiFeaturesImportCompatibilityCodes.ManualReview)
            : MigrationInventoryHelpers.Compatible(
                $"OGC API Features {dependencyType} link was captured for migration planning.",
                code: OgcApiFeaturesImportCompatibilityCodes.CollectionSource);

        return new MigrationExternalDependency
        {
            Id = BuildDependencyId($"{resourceId}:{dependencyType}", address ?? link.Href),
            ContainerId = ContainerId,
            ResourceId = resourceId,
            Kind = $"ogc-api-features-{dependencyType}",
            Name = BuildLinkName(link, dependencyType),
            DependencyType = dependencyType,
            Address = address,
            Metadata = BuildLinkMetadata(link, baseUri),
            Compatibility = compatibility
        };
    }

    private static IEnumerable<MigrationExternalDependency> BuildConformanceDependencies(IEnumerable<string> conformanceClasses)
    {
        foreach (var conformanceClass in MigrationInventoryHelpers.NormalizeStrings(conformanceClasses))
        {
            var isTransactional = IsTransactionalConformance(conformanceClass);
            var isVendorExtension = IsVendorConformanceClass(conformanceClass);

            yield return new MigrationExternalDependency
            {
                Id = $"conformance:{ToStableId(conformanceClass)}",
                ContainerId = ContainerId,
                Kind = "ogc-api-features-conformance",
                Name = conformanceClass,
                DependencyType = isTransactional ? "transactions" : isVendorExtension ? "vendor-extension" : "conformance",
                Metadata = new Dictionary<string, string>
                {
                    ["conformanceClass"] = conformanceClass
                },
                Compatibility = isTransactional
                    ? MigrationInventoryHelpers.Partial(
                        "OGC API Features transaction conformance needs migration cutover review.",
                        manualSteps: ["Confirm source-side create, replace, update, or delete operations are not required after migration cutover."],
                        code: OgcApiFeaturesImportCompatibilityCodes.TransactionsManualReview)
                    : isVendorExtension
                        ? MigrationInventoryHelpers.Partial(
                            "Vendor conformance class needs source-specific migration review.",
                            manualSteps: ["Classify the vendor conformance class as automated, assisted, manual-review, or unsupported."],
                            code: OgcApiFeaturesImportCompatibilityCodes.VendorExtensionManualReview)
                        : MigrationInventoryHelpers.Compatible(
                            "OGC API Features conformance class was captured for migration planning.",
                            code: OgcApiFeaturesImportCompatibilityCodes.CollectionSource)
            };
        }
    }

    private static IEnumerable<MigrationExternalDependency> BuildVendorExtensionDependencies(IEnumerable<string> vendorExtensions, string? resourceId)
    {
        foreach (var extension in MigrationInventoryHelpers.NormalizeStrings(vendorExtensions))
        {
            yield return new MigrationExternalDependency
            {
                Id = resourceId == null
                    ? $"extension:{ToStableId(extension)}"
                    : $"{resourceId}:extension:{ToStableId(extension)}",
                ContainerId = ContainerId,
                ResourceId = resourceId,
                Kind = "ogc-api-features-extension",
                Name = extension,
                DependencyType = "vendor-extension",
                Metadata = new Dictionary<string, string>
                {
                    ["extension"] = extension
                },
                Compatibility = MigrationInventoryHelpers.Partial(
                    "Vendor extension needs source-specific migration review.",
                    manualSteps: ["Classify the extension as automated, assisted, manual-review, or unsupported."],
                    code: OgcApiFeaturesImportCompatibilityCodes.VendorExtensionManualReview)
            };
        }
    }

    private static MigrationInventoryCompleteness BuildCompleteness(
        MigrationInventoryResource[] resources,
        MigrationExternalDependency[] dependencies)
    {
        var warnings = resources.SelectMany(static resource => resource.Compatibility.Warnings)
            .Concat(dependencies.SelectMany(static dependency => dependency.Compatibility.Warnings))
            .ToArray();
        var missingArtifacts = resources
            .SelectMany(static resource => BuildMissingArtifacts(resource))
            .ToArray();
        var status = resources.Length == 0 || resources.Any(static resource => resource.Compatibility.Level == "incompatible")
            ? "partial"
            : warnings.Length > 0 || missingArtifacts.Length > 0 ||
              dependencies.Any(static dependency => dependency.Compatibility.Level == "partial")
                ? "partial"
                : "complete";

        return MigrationInventoryHelpers.BuildCompleteness(status, warnings, missingArtifacts);
    }

    private static IEnumerable<string> BuildMissingArtifacts(MigrationInventoryResource resource)
    {
        if (!resource.Capabilities.Contains("ogcapi-features:queryables", StringComparer.Ordinal))
        {
            yield return $"queryables:{resource.Name}";
        }

        if (!resource.Capabilities.Contains("ogcapi-features:schema", StringComparer.Ordinal))
        {
            yield return $"schema:{resource.Name}";
        }

        if (!resource.Capabilities.Contains("ogcapi-features:pagination", StringComparer.Ordinal))
        {
            yield return $"pagination:{resource.Name}";
        }
    }

    private static MigrationCompatibilityAssessment BuildContainerCompatibility(
        MigrationInventoryResource[] resources,
        MigrationExternalDependency[] dependencies)
    {
        if (resources.Length == 0)
        {
            return MigrationInventoryHelpers.Partial(
                "OGC API Features landing page was captured, but no collections were discovered.",
                manualSteps: ["Confirm the collections endpoint is reachable and returns at least one collection."],
                code: OgcApiFeaturesImportCompatibilityCodes.ManualReview);
        }

        return MigrationInventoryHelpers.Aggregate(
            resources.Select(static resource => resource.Compatibility)
                .Concat(dependencies.Select(static dependency => dependency.Compatibility)),
            "No OGC API Features collections were discovered.");
    }

    private static string[] BuildCollectionCapabilities(
        OgcApiFeaturesLink[] itemLinks,
        string[] itemEncodings,
        OgcApiFeaturesLink[] queryablesLinks,
        OgcApiFeaturesLink[] schemaLinks,
        MigrationSpatialReferenceInfo[] spatialReferences,
        OgcApiFeaturesLink[] paginationLinks,
        bool hasTransactionalConformance,
        string[] vendorExtensions)
    {
        var capabilities = new List<string>
        {
            "ogcapi-features:landing-page",
            "ogcapi-features:conformance",
            "ogcapi-features:collections"
        };

        if (itemLinks.Length > 0 || paginationLinks.Length > 0)
        {
            capabilities.Add("ogcapi-features:items");
        }

        if (itemEncodings.Any(IsJsonEncoding))
        {
            capabilities.Add("ogcapi-features:geojson-items");
        }

        if (queryablesLinks.Length > 0)
        {
            capabilities.Add("ogcapi-features:queryables");
        }

        if (schemaLinks.Length > 0)
        {
            capabilities.Add("ogcapi-features:schema");
        }

        if (spatialReferences.Length > 0)
        {
            capabilities.Add("ogcapi-features:crs");
        }

        if (paginationLinks.Length > 0)
        {
            capabilities.Add("ogcapi-features:pagination");
        }

        if (hasTransactionalConformance)
        {
            capabilities.Add("ogcapi-features:transactions");
        }

        if (vendorExtensions.Length > 0)
        {
            capabilities.Add("ogcapi-features:vendor-extension");
        }

        return MigrationInventoryHelpers.NormalizeStrings(capabilities);
    }

    private static string[] BuildRelatedDependencyIds(OgcApiFeaturesCollectionSnapshot collection, string resourceId, Uri baseUri)
    {
        return collection.Links.Concat(collection.PaginationLinks)
            .Select(link => ClassifyCollectionLink(link) is { } dependencyType
                ? BuildDependencyId($"{resourceId}:{dependencyType}", NormalizeAddress(link.Href, baseUri) ?? link.Href)
                : null)
            .Where(static value => value != null)
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static MigrationSpatialReferenceInfo[] BuildSpatialReferences(OgcApiFeaturesCrsDeclaration[] declarations)
    {
        return declarations
            .Where(static declaration => !string.IsNullOrWhiteSpace(declaration.Value))
            .OrderBy(static declaration => declaration.Role, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Value, StringComparer.Ordinal)
            .Select(static declaration =>
            {
                var srid = TryParseSrid(declaration.Value);
                return new MigrationSpatialReferenceInfo
                {
                    Role = string.IsNullOrWhiteSpace(declaration.Role) ? "declared" : declaration.Role.Trim(),
                    SourceValue = declaration.Value.Trim(),
                    Srid = srid,
                    CrsUri = NormalizeCrsUri(declaration.Value, srid),
                    AxisOrder = IsCrs84(declaration.Value) ? "east-north" : null,
                    IsGeographic = IsCrs84(declaration.Value) || srid is >= 4000 and <= 4999
                };
            })
            .ToArray();
    }

    private static Dictionary<string, string> BuildLinkMetadata(OgcApiFeaturesLink link, Uri baseUri)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["rel"] = link.Rel.Trim()
        };

        if (!string.IsNullOrWhiteSpace(link.Type))
        {
            metadata["type"] = link.Type.Trim();
        }

        if (!string.IsNullOrWhiteSpace(link.Title))
        {
            metadata["title"] = link.Title.Trim();
        }

        var queryParameterKeys = GetQueryParameterKeys(link.Href, baseUri);
        if (queryParameterKeys.Length > 0)
        {
            metadata["queryParameters"] = string.Join(",", queryParameterKeys);
        }

        return metadata.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal);
    }

    private static IEnumerable<OgcApiFeaturesLink> GetItemLinks(OgcApiFeaturesCollectionSnapshot collection)
        => collection.Links.Where(static link => IsItemsRel(link.Rel) || LinkLooksLikeItemsEndpoint(link));

    private static IEnumerable<OgcApiFeaturesLink> GetQueryablesLinks(IEnumerable<OgcApiFeaturesLink> links)
        => links.Where(static link =>
            string.Equals(link.Rel, "queryables", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(link.Rel, "http://www.opengis.net/def/rel/ogc/1.0/queryables", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<OgcApiFeaturesLink> GetSchemaLinks(IEnumerable<OgcApiFeaturesLink> links)
        => links.Where(static link =>
            string.Equals(link.Rel, "describedby", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(link.Rel, "schema", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(link.Rel, "http://www.opengis.net/def/rel/ogc/1.0/schema", StringComparison.OrdinalIgnoreCase));

    private static string[] BuildItemEncodings(
        OgcApiFeaturesCollectionSnapshot collection,
        OgcApiFeaturesLink[] itemLinks,
        OgcApiFeaturesLink[] paginationLinks)
        => MigrationInventoryHelpers.NormalizeStrings(
            collection.ItemEncodings
                .Concat(BuildItemEncodings(itemLinks))
                .Concat(BuildItemEncodings(paginationLinks)));

    private static string[] BuildItemEncodings(IEnumerable<OgcApiFeaturesLink> itemLinks)
        => MigrationInventoryHelpers.NormalizeStrings(
            itemLinks.Select(static link => link.Type)
                .Where(static type => !string.IsNullOrWhiteSpace(type))
                .Select(static type => type!));

    private static string[] GetUnknownLinkRelations(IEnumerable<OgcApiFeaturesLink> links)
        => links.Select(static link => link.Rel)
            .Where(static rel => !string.IsNullOrWhiteSpace(rel))
            .Where(static rel => !KnownCollectionLinkRelations.Contains(rel, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static rel => rel, StringComparer.Ordinal)
            .ToArray();

    private static string? ClassifyCollectionLink(OgcApiFeaturesLink link)
    {
        if (IsPaginationRel(link.Rel))
        {
            return "pagination";
        }

        if (IsItemsRel(link.Rel) || LinkLooksLikeItemsEndpoint(link))
        {
            return "items";
        }

        if (GetQueryablesLinks([link]).Any())
        {
            return "queryables";
        }

        if (GetSchemaLinks([link]).Any())
        {
            return "schema";
        }

        if (!KnownCollectionLinkRelations.Contains(link.Rel, StringComparer.OrdinalIgnoreCase))
        {
            return "manual-review-link";
        }

        return null;
    }

    private static bool IsLandingPagePlanningLink(OgcApiFeaturesLink link)
        => string.Equals(link.Rel, "conformance", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(link.Rel, "data", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(link.Rel, "service-desc", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(link.Rel, "service-doc", StringComparison.OrdinalIgnoreCase);

    private static bool IsItemsRel(string rel)
        => string.Equals(rel, "items", StringComparison.OrdinalIgnoreCase);

    private static bool IsPaginationRel(string rel)
        => string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(rel, "prev", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(rel, "previous", StringComparison.OrdinalIgnoreCase);

    private static bool LinkLooksLikeItemsEndpoint(OgcApiFeaturesLink link)
        => link.Href.Contains("/items", StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonEncoding(string? encoding)
        => !string.IsNullOrWhiteSpace(encoding) &&
           (encoding.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            encoding.Contains("geo+json", StringComparison.OrdinalIgnoreCase));

    private static bool IsHtmlEncoding(string? encoding)
        => !string.IsNullOrWhiteSpace(encoding) &&
           encoding.Contains("html", StringComparison.OrdinalIgnoreCase);

    private static bool IsPortableDefaultCrs(MigrationSpatialReferenceInfo spatialReference)
        => spatialReference.Srid == 4326 || IsCrs84(spatialReference.SourceValue) || IsCrs84(spatialReference.CrsUri);

    private static bool HasTransactionalConformance(IEnumerable<string> conformanceClasses)
        => conformanceClasses.Any(IsTransactionalConformance);

    private static bool IsTransactionalConformance(string conformanceClass)
        => conformanceClass.Contains("ogcapi-features-4", StringComparison.OrdinalIgnoreCase) ||
           conformanceClass.Contains("transaction", StringComparison.OrdinalIgnoreCase) ||
           conformanceClass.Contains("create-replace-delete", StringComparison.OrdinalIgnoreCase);

    private static bool IsVendorConformanceClass(string conformanceClass)
        => Uri.TryCreate(conformanceClass, UriKind.Absolute, out var uri) &&
           !uri.Host.Contains("opengis.net", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeCrsUri(string value, int? srid)
    {
        if (IsCrs84(value))
        {
            return "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
        }

        if (srid.HasValue)
        {
            return $"http://www.opengis.net/def/crs/EPSG/0/{srid.Value}";
        }

        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value.Trim() : null;
    }

    private static bool IsCrs84(string? value)
        => value?.Contains("CRS84", StringComparison.OrdinalIgnoreCase) == true;

    private static int? TryParseSrid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (IsCrs84(value))
        {
            return null;
        }

        var digits = new string(value.Reverse()
            .TakeWhile(static character => char.IsDigit(character))
            .Reverse()
            .ToArray());

        return int.TryParse(digits, out var srid) ? srid : null;
    }

    private static string[] GetQueryParameterKeys(string href, Uri baseUri)
    {
        var uri = ResolveUri(href, baseUri);
        if (uri == null || string.IsNullOrWhiteSpace(uri.Query))
        {
            return [];
        }

        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => segment.Split('=', 2)[0])
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildLinkName(OgcApiFeaturesLink link, string fallback)
        => string.IsNullOrWhiteSpace(link.Title) ? fallback : link.Title.Trim();

    private static string GetMetadataValue(MigrationExternalDependency dependency, string key)
        => dependency.Metadata.GetValueOrDefault(key, string.Empty);

    private static string NormalizeRel(string rel)
        => string.IsNullOrWhiteSpace(rel) ? "link" : ToStableId(rel);

    private static string BuildDependencyId(string prefix, string address)
        => MigrationInventoryHelpers.BuildExternalDependencyId(prefix, address);

    private static string? NormalizeAddress(string href, Uri baseUri)
    {
        var uri = ResolveUri(href, baseUri);
        return uri == null
            ? MigrationInventoryHelpers.NormalizeExternalAddress(href)
            : MigrationInventoryHelpers.NormalizeExternalAddress(uri.ToString());
    }

    private static Uri? ResolveUri(string href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var absolute)
            ? absolute
            : Uri.TryCreate(baseUri, href, out var relative)
                ? relative
                : null;
    }

    private static Uri CreateBaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("OGC API Features base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        }

        return baseUri;
    }

    private static string ToStableId(string value)
    {
        var characters = value.Trim()
            .Select(static character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray();
        var compact = new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .DefaultIfEmpty("item");

        return string.Join("-", compact);
    }
}
