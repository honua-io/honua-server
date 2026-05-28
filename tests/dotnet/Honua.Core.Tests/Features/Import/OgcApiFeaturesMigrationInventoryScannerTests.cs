// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class OgcApiFeaturesMigrationInventoryScannerTests
{
    [Fact]
    public void BuildInventory_WithGeoJsonCollection_ModelsOgcApiFeaturesScanFacts()
    {
        var artifact = OgcApiFeaturesMigrationInventoryScanner.BuildInventory(new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = "https://demo.example/ogcapi/",
            Title = "Demo OGC API Features",
            Version = "1.0",
            LandingPageLinks =
            [
                Link("service-desc", "https://demo.example/ogcapi/openapi", "application/vnd.oai.openapi+json;version=3.0"),
                Link("conformance", "https://demo.example/ogcapi/conformance", "application/json"),
                Link("data", "https://demo.example/ogcapi/collections", "application/json")
            ],
            ConformanceClasses =
            [
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables"
            ],
            Collections =
            [
                new OgcApiFeaturesCollectionSnapshot
                {
                    Id = "roads",
                    Title = "Roads",
                    GeometryType = "LineString",
                    FeatureCount = 125,
                    Links =
                    [
                        Link("items", "https://demo.example/ogcapi/collections/roads/items", "application/geo+json"),
                        Link("http://www.opengis.net/def/rel/ogc/1.0/queryables", "https://demo.example/ogcapi/collections/roads/queryables", "application/schema+json"),
                        Link("describedby", "https://demo.example/ogcapi/collections/roads/schema", "application/schema+json")
                    ],
                    PaginationLinks =
                    [
                        Link("next", "https://demo.example/ogcapi/collections/roads/items?offset=100&limit=100", "application/geo+json")
                    ],
                    CrsDeclarations =
                    [
                        Crs("storage", "http://www.opengis.net/def/crs/OGC/1.3/CRS84"),
                        Crs("supported", "http://www.opengis.net/def/crs/EPSG/0/4326")
                    ],
                    ItemEncodings = ["application/geo+json"],
                    Fields =
                    [
                        new MigrationInventoryField
                        {
                            Name = "name",
                            Alias = "Road name",
                            FieldType = "string",
                            Nullable = false
                        }
                    ]
                }
            ]
        });

        artifact.SourceKind.Should().Be("ogc-api-features");
        artifact.Source.ServiceType.Should().Be("OGC API Features");
        artifact.ScanCompleteness.Status.Should().Be("complete");
        artifact.OverallCompatibility.Level.Should().Be("compatible");

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        resource.Id.Should().Be("collection:roads");
        resource.Kind.Should().Be("ogc-api-features-collection");
        resource.FeatureCount.Should().Be(125);
        resource.Capabilities.Should().Contain(
            "ogcapi-features:items",
            "ogcapi-features:geojson-items",
            "ogcapi-features:queryables",
            "ogcapi-features:schema",
            "ogcapi-features:pagination",
            "ogcapi-features:crs");
        resource.SpatialReferences.Should().Contain(reference => reference.Role == "supported" && reference.Srid == 4326);
        resource.SpatialReferences.Should().Contain(reference => reference.Role == "storage" && reference.CrsUri == "http://www.opengis.net/def/crs/OGC/1.3/CRS84");
        resource.Fields.Should().ContainSingle(field => field.Name == "name" && field.Alias == "Road name");
        resource.Compatibility.Code.Should().Be(OgcApiFeaturesImportCompatibilityCodes.CollectionSource);
        artifact.FidelityClassifications.Should().Contain(record =>
            record.SourceId == resource.Id &&
            record.Category == "target-exposure" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.Automated &&
            record.Metadata["targetProtocols"] == "OGC API Features,FeatureServer");

        var pagination = artifact.ExternalDependencies.Should()
            .ContainSingle(dependency => dependency.ResourceId == resource.Id && dependency.DependencyType == "pagination")
            .Subject;
        pagination.Address.Should().Be("https://demo.example/ogcapi/collections/roads/items");
        pagination.Metadata.Should().ContainKey("queryParameters").WhoseValue.Should().Be("limit,offset");
        resource.ExternalDependencyIds.Should().Contain(pagination.Id);
    }

    [Fact]
    public void BuildInventory_WithNonJsonItems_MarksCollectionUnsupported()
    {
        var artifact = OgcApiFeaturesMigrationInventoryScanner.BuildInventory(new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = "https://demo.example/ogcapi/",
            Title = "Demo OGC API Features",
            Collections =
            [
                new OgcApiFeaturesCollectionSnapshot
                {
                    Id = "parcels",
                    Links =
                    [
                        Link("items", "https://demo.example/ogcapi/collections/parcels/items", "application/gml+xml"),
                        Link("queryables", "https://demo.example/ogcapi/collections/parcels/queryables", "application/schema+json"),
                        Link("describedby", "https://demo.example/ogcapi/collections/parcels/schema", "application/schema+json")
                    ],
                    PaginationLinks =
                    [
                        Link("next", "https://demo.example/ogcapi/collections/parcels/items?startIndex=100", "application/gml+xml")
                    ],
                    CrsDeclarations = [Crs("storage", "http://www.opengis.net/def/crs/EPSG/0/4326")],
                    ItemEncodings = ["application/gml+xml"]
                }
            ]
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        artifact.ScanCompleteness.Status.Should().Be("partial");
        artifact.OverallCompatibility.Level.Should().Be("incompatible");
        resource.Compatibility.Level.Should().Be("incompatible");
        resource.Compatibility.Code.Should().Be(OgcApiFeaturesImportCompatibilityCodes.NonJsonItemsEncoding);
        resource.Compatibility.ManualSteps.Should().Contain(step => step.Contains("GeoJSON", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInventory_WithProbedPaginationButNoItemsLink_TreatsItemsEndpointAsSupported()
    {
        var artifact = OgcApiFeaturesMigrationInventoryScanner.BuildInventory(new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = "https://demo.example/ogcapi/",
            Title = "Demo OGC API Features",
            Collections =
            [
                new OgcApiFeaturesCollectionSnapshot
                {
                    Id = "buildings",
                    Links =
                    [
                        Link("queryables", "https://demo.example/ogcapi/collections/buildings/queryables", "application/schema+json"),
                        Link("describedby", "https://demo.example/ogcapi/collections/buildings/schema", "application/schema+json")
                    ],
                    PaginationLinks =
                    [
                        Link("next", "https://demo.example/ogcapi/collections/buildings/items?offset=100&limit=100", "application/geo+json")
                    ],
                    CrsDeclarations = [Crs("storage", "http://www.opengis.net/def/crs/EPSG/0/4326")]
                }
            ]
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        resource.Capabilities.Should().Contain("ogcapi-features:items");
        resource.Capabilities.Should().Contain("ogcapi-features:geojson-items");
        resource.Compatibility.Level.Should().Be("compatible");
        resource.Compatibility.Code.Should().Be(OgcApiFeaturesImportCompatibilityCodes.CollectionSource);
        resource.Compatibility.Code.Should().NotBe(OgcApiFeaturesImportCompatibilityCodes.MissingItemsEndpoint);
    }

    [Fact]
    public void BuildInventory_WithDuplicateCollectionLinks_EmitsUniqueDependencyIds()
    {
        var artifact = OgcApiFeaturesMigrationInventoryScanner.BuildInventory(new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = "https://demo.example/ogcapi/",
            Title = "Demo OGC API Features",
            Collections =
            [
                new OgcApiFeaturesCollectionSnapshot
                {
                    Id = "roads",
                    Links =
                    [
                        Link("items", "https://demo.example/ogcapi/collections/roads/items", "application/geo+json"),
                        Link("items", "https://demo.example/ogcapi/collections/roads/items", "text/html"),
                        Link("queryables", "https://demo.example/ogcapi/collections/roads/queryables", "application/schema+json"),
                        Link("describedby", "https://demo.example/ogcapi/collections/roads/schema", "application/schema+json")
                    ],
                    PaginationLinks =
                    [
                        Link("next", "https://demo.example/ogcapi/collections/roads/items?offset=100&limit=100", "application/geo+json"),
                        Link("prev", "https://demo.example/ogcapi/collections/roads/items?offset=0&limit=100", "application/geo+json")
                    ],
                    CrsDeclarations = [Crs("storage", "http://www.opengis.net/def/crs/EPSG/0/4326")],
                    ItemEncodings = ["application/geo+json"]
                }
            ]
        });

        var dependencyIds = artifact.ExternalDependencies.Select(static dependency => dependency.Id).ToArray();
        dependencyIds.Should().OnlyHaveUniqueItems();

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        var collectionDependencies = artifact.ExternalDependencies
            .Where(dependency => dependency.ResourceId == resource.Id)
            .ToArray();

        collectionDependencies.Should().ContainSingle(dependency => dependency.DependencyType == "items");
        collectionDependencies.Should().ContainSingle(dependency => dependency.DependencyType == "pagination");
        resource.ExternalDependencyIds.Should().BeEquivalentTo(collectionDependencies.Select(static dependency => dependency.Id));
    }

    [Fact]
    public void BuildInventory_WithTransactionsVendorExtensionsAndMissingLinks_ReturnsManualReviewWarnings()
    {
        var artifact = OgcApiFeaturesMigrationInventoryScanner.BuildInventory(new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = "https://demo.example/ogcapi/",
            Title = "Vendor OGC API Features",
            VendorExtensions = ["x-service-cache"],
            ConformanceClasses =
            [
                "http://www.opengis.net/spec/ogcapi-features-4/1.0/conf/create-replace-delete",
                "https://vendor.example/spec/custom-ogcapi/1.0/conf/link-templates"
            ],
            Collections =
            [
                new OgcApiFeaturesCollectionSnapshot
                {
                    Id = "assets",
                    Links =
                    [
                        Link("items", "https://demo.example/ogcapi/collections/assets/items", "application/geo+json"),
                        Link("https://vendor.example/rel/tilejson", "https://demo.example/tiles/assets/tilejson.json", "application/json")
                    ],
                    CrsDeclarations = [Crs("storage", "https://vendor.example/crs/local-grid")],
                    ItemEncodings = ["application/geo+json", "application/vnd.flatgeobuf"],
                    VendorExtensions = ["x-symbol-library"]
                }
            ]
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        resource.Compatibility.Level.Should().Be("partial");
        resource.Compatibility.Code.Should().Be(OgcApiFeaturesImportCompatibilityCodes.ManualReview);
        resource.Compatibility.Warnings.Should().Contain(warning => warning.Contains("queryables", StringComparison.Ordinal));
        resource.Compatibility.Warnings.Should().Contain(warning => warning.Contains("schema", StringComparison.Ordinal));
        resource.Compatibility.Warnings.Should().Contain(warning => warning.Contains("pagination", StringComparison.Ordinal));
        resource.Compatibility.Warnings.Should().Contain(warning => warning.Contains("CRS", StringComparison.Ordinal));
        resource.Compatibility.Warnings.Should().Contain(warning => warning.Contains("link relations", StringComparison.Ordinal));
        resource.Compatibility.Warnings.Should().Contain(warning => warning.Contains("vendor extensions", StringComparison.Ordinal));
        resource.Compatibility.Warnings.Should().Contain(warning => warning.Contains("transaction", StringComparison.OrdinalIgnoreCase));
        artifact.ScanCompleteness.MissingArtifacts.Should().BeEquivalentTo(
            "pagination:assets",
            "queryables:assets",
            "schema:assets");
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.DependencyType == "transactions" &&
            dependency.Compatibility.Code == OgcApiFeaturesImportCompatibilityCodes.TransactionsManualReview);
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.DependencyType == "vendor-extension" &&
            dependency.Compatibility.Code == OgcApiFeaturesImportCompatibilityCodes.VendorExtensionManualReview);
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.DependencyType == "manual-review-link" &&
            dependency.Compatibility.Code == OgcApiFeaturesImportCompatibilityCodes.ManualReview);
        artifact.FidelityClassifications.Should().Contain(record =>
            record.Category == "vendor-extension" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.ManualReview);
    }

    [Fact]
    public async Task ScanSourceAsync_WithFixtureSource_CapturesCollectionsSchemaPagingAndItems()
    {
        using var httpClient = new HttpClient(new FixtureOgcApiFeaturesHandler())
        {
            BaseAddress = new Uri("https://demo.example")
        };
        var scanner = new OgcApiFeaturesMigrationScanner(
            httpClient,
            NullLogger<OgcApiFeaturesMigrationScanner>.Instance,
            static (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]));

        var artifact = await scanner.ScanSourceAsync(new OgcApiFeaturesScanRequest
        {
            ServiceUrl = "https://demo.example/ogcapi/",
            TimeoutSeconds = 5
        });

        artifact.SourceKind.Should().Be("ogc-api-features");
        artifact.AuthPosture.Mode.Should().Be("anonymous");
        artifact.ScanCompleteness.Status.Should().Be("complete");
        artifact.OverallCompatibility.Level.Should().Be("compatible");

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        resource.Name.Should().Be("roads");
        resource.GeometryType.Should().Be("Point");
        resource.FeatureCount.Should().Be(2);
        resource.Fields.Should().Contain(field => field.Name == "name" && field.Alias == "Road name");
        resource.Fields.Should().Contain(field => field.Name == "speed" && field.FieldType == "integer");
        resource.Capabilities.Should().Contain(
            "ogcapi-features:items",
            "ogcapi-features:queryables",
            "ogcapi-features:schema",
            "ogcapi-features:pagination");
        resource.ExternalDependencyIds.Should().Contain(id => id.Contains("pagination", StringComparison.Ordinal));

        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.DependencyType == "pagination" &&
            dependency.Metadata["queryParameters"] == "limit,offset");
        artifact.FidelityClassifications.Should().Contain(record =>
            record.SourceId == resource.Id &&
            record.Category == "feature-data" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.Automated);
    }

    [Fact]
    public async Task ScanSourceAsync_WithSameHostRedirect_FollowsRedirectAndScans()
    {
        using var httpClient = new HttpClient(new FixtureOgcApiFeaturesHandler())
        {
            BaseAddress = new Uri("https://demo.example")
        };
        var scanner = new OgcApiFeaturesMigrationScanner(
            httpClient,
            NullLogger<OgcApiFeaturesMigrationScanner>.Instance,
            static (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]));

        var artifact = await scanner.ScanSourceAsync(new OgcApiFeaturesScanRequest
        {
            ServiceUrl = "https://demo.example/redirect",
            TimeoutSeconds = 5
        });

        artifact.ScanCompleteness.Status.Should().Be("complete");
        artifact.Resources.Should().ContainSingle(resource => resource.Name == "roads");
    }

    [Fact]
    public async Task ScanSourceAsync_WithCrossHostRedirect_BlocksRedirectWithoutForwardingAuthorization()
    {
        var handler = new FixtureOgcApiFeaturesHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://demo.example")
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        var scanner = new OgcApiFeaturesMigrationScanner(
            httpClient,
            NullLogger<OgcApiFeaturesMigrationScanner>.Instance,
            static (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]));

        var artifact = await scanner.ScanSourceAsync(new OgcApiFeaturesScanRequest
        {
            ServiceUrl = "https://demo.example/external-redirect",
            TimeoutSeconds = 5
        });

        artifact.ScanCompleteness.Status.Should().Be("failed");
        handler.RequestUris.Should().ContainSingle(uri => uri.Host == "demo.example");
        handler.RequestUris.Should().NotContain(uri => uri.Host == "other.example");
    }

    [Fact]
    public void Translate_WithOgcApiFeaturesInventory_EmitsFeatureImportTarget()
    {
        var inventory = OgcApiFeaturesMigrationInventoryScanner.BuildInventory(new OgcApiFeaturesMigrationSourceSnapshot
        {
            BaseUrl = "https://demo.example/ogcapi/",
            Title = "Demo OGC API Features",
            Collections =
            [
                new OgcApiFeaturesCollectionSnapshot
                {
                    Id = "roads",
                    Links =
                    [
                        Link("items", "https://demo.example/ogcapi/collections/roads/items", "application/geo+json"),
                        Link("queryables", "https://demo.example/ogcapi/collections/roads/queryables", "application/schema+json"),
                        Link("describedby", "https://demo.example/ogcapi/collections/roads/schema", "application/schema+json")
                    ],
                    PaginationLinks =
                    [
                        Link("next", "https://demo.example/ogcapi/collections/roads/items?offset=100&limit=100", "application/geo+json")
                    ],
                    CrsDeclarations = [Crs("storage", "http://www.opengis.net/def/crs/EPSG/0/4326")],
                    ItemEncodings = ["application/geo+json"]
                }
            ]
        });

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Migrated OAPIF"
        });
        var evidence = MigrationParityEvidenceGenerator.Generate(inventory, manifest);

        var target = manifest.TargetResources.Should().ContainSingle().Subject;
        target.MigrationMode.Should().Be("feature-import");
        target.SourceProtocol.Should().Be("OGC API Features");
        target.Capabilities.Should().Contain("ogcapi-features:geojson-items");
        evidence.ManifestAvailable.Should().BeTrue();
        evidence.Sections.Should().Contain(section => section.Id == "fidelity")
            .Which.Items.Should().Contain(item => item.Id.Contains("target-exposure", StringComparison.Ordinal));
    }

    private static OgcApiFeaturesLink Link(string rel, string href, string? type = null)
        => new()
        {
            Rel = rel,
            Href = href,
            Type = type
        };

    private static OgcApiFeaturesCrsDeclaration Crs(string role, string value)
        => new()
        {
            Role = role,
            Value = value
        };

    private sealed class FixtureOgcApiFeaturesHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.Should().NotBeNull();
            RequestUris.Add(request.RequestUri!);
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            var json = pathAndQuery switch
            {
                "/ogcapi/" => """
                    {
                      "title": "Fixture OGC API Features",
                      "version": "1.0.0",
                      "links": [
                        { "rel": "self", "href": "https://demo.example/ogcapi/", "type": "application/json" },
                        { "rel": "conformance", "href": "https://demo.example/ogcapi/conformance", "type": "application/json" },
                        { "rel": "data", "href": "https://demo.example/ogcapi/collections", "type": "application/json" }
                      ]
                    }
                    """,
                "/ogcapi/conformance" => """
                    {
                      "conformsTo": [
                        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
                        "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables"
                      ]
                    }
                    """,
                "/ogcapi/collections" => """
                    {
                      "collections": [
                        {
                          "id": "roads",
                          "title": "Roads",
                          "description": "Reference road centerlines",
                          "storageCrs": "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
                          "crs": ["http://www.opengis.net/def/crs/EPSG/0/4326"],
                          "links": [
                            { "rel": "items", "href": "https://demo.example/ogcapi/collections/roads/items", "type": "application/geo+json" },
                            { "rel": "queryables", "href": "https://demo.example/ogcapi/collections/roads/queryables", "type": "application/schema+json" },
                            { "rel": "describedby", "href": "https://demo.example/ogcapi/collections/roads/schema", "type": "application/schema+json" }
                          ]
                        }
                      ]
                    }
                    """,
                "/ogcapi/collections/roads/queryables" => """
                    {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string", "title": "Road name", "nullable": false },
                        "speed": { "type": "integer" }
                      }
                    }
                    """,
                "/ogcapi/collections/roads/schema" => """
                    {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string", "title": "Road name" },
                        "speed": { "type": "integer" }
                      }
                    }
                    """,
                "/ogcapi/collections/roads/items?limit=1" => """
                    {
                      "type": "FeatureCollection",
                      "numberMatched": 2,
                      "numberReturned": 1,
                      "links": [
                        { "rel": "self", "href": "https://demo.example/ogcapi/collections/roads/items?limit=1", "type": "application/geo+json" },
                        { "rel": "next", "href": "https://demo.example/ogcapi/collections/roads/items?offset=1&limit=1", "type": "application/geo+json" }
                      ],
                      "features": [
                        {
                          "type": "Feature",
                          "id": "road.1",
                          "geometry": { "type": "Point", "coordinates": [-157.8583, 21.3069] },
                          "properties": { "name": "King", "speed": 25 }
                        }
                      ]
                    }
                    """,
                _ => null
            };

            if (pathAndQuery == "/redirect")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("/ogcapi/", UriKind.Relative) }
                });
            }

            if (pathAndQuery == "/external-redirect")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://other.example/ogcapi/") }
                });
            }

            return Task.FromResult(json == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
        }
    }
}
