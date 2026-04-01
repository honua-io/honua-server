// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Metadata.Schema;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoServerMigrationManifestServiceTests
{
    [Fact]
    public async Task TranslateAsync_WithPostGisVectorLayer_ProducesDeterministicManifest()
    {
        var serviceInfo = new GeoServerServiceInfo
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            Version = "2.28.0",
            Workspaces =
            [
                new GeoServerWorkspaceInfo
                {
                    Name = "demo"
                }
            ],
            DataStores =
            [
                new GeoServerDataStoreInfo
                {
                    Name = "states",
                    WorkspaceName = "demo",
                    Type = "PostGIS",
                    ConnectionParameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["host"] = "db.internal",
                        ["port"] = "5432",
                        ["database"] = "gis",
                        ["schema"] = "public",
                        ["user"] = "honua_reader",
                        ["passwd"] = "db-password-123"
                    }
                }
            ],
            Layers =
            [
                new GeoServerLayerInfo
                {
                    Name = "states",
                    WorkspaceName = "demo",
                    DataStoreName = "states",
                    NativeName = "public.states",
                    SRS = "EPSG:4326",
                    GeometryColumn = "geom",
                    GeometryType = "MultiPolygon",
                    DefaultStyle = "polygon"
                }
            ],
            Styles =
            [
                new GeoServerStyleInfo
                {
                    Name = "polygon",
                    Format = "sld",
                    SldContent = "<StyledLayerDescriptor version=\"1.0.0\" />"
                }
            ],
            CompatibilityAssessment = new GeoServerMigrationCompatibility
            {
                FullyCompatibleResources = 2,
                IncompatibleResources = 1
            }
        };

        var importService = new StubGeoServerImportService(serviceInfo);
        var sut = new GeoServerMigrationManifestService(importService, NullLogger<GeoServerMigrationManifestService>.Instance);
        var request = new GeoServerTranslationRequest
        {
            GeoServerRestUrl = serviceInfo.GeoServerRestUrl,
            Username = "admin",
            Password = "geoserver-password-123",
            ImportStyles = true,
            IncludeStyleContent = true,
            ImportOptions = new GeoServerImportOptions
            {
                WorkspaceNameMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["demo"] = "coastal"
                }
            }
        };

        var manifest1 = await sut.TranslateAsync(request);
        await Task.Delay(TimeSpan.FromMilliseconds(25));
        var manifest2 = await sut.TranslateAsync(request);

        manifest1.ApiVersion.Should().Be(MigrationManifestVersions.V1Alpha1);
        manifest1.SourceType.Should().Be(MigrationSourceType.GeoServer);
        manifest1.ManifestHash.Should().Be(manifest2.ManifestHash);
        MigrationManifestHasher.ComputeHash(manifest1).Should().Be(manifest1.ManifestHash);
        MigrationManifestHasher.ComputeHash(manifest2).Should().Be(manifest2.ManifestHash);

        manifest1.ConnectionDrafts.Should().ContainSingle();
        var connectionDraft = manifest1.ConnectionDrafts.Single();
        connectionDraft.Alias.Should().Be("demo:states");
        connectionDraft.Host.Should().Be("db.internal");
        connectionDraft.DatabaseName.Should().Be("gis");
        connectionDraft.SchemaName.Should().Be("public");
        connectionDraft.UsernameHint.Should().Be("honua_reader");
        connectionDraft.SecretRequirements.Should().ContainSingle()
            .Which.Kind.Should().Be(MigrationSecretRequirementKind.Password);

        manifest1.PublishPlan.Should().ContainSingle();
        var publishPlan = manifest1.PublishPlan.Single();
        publishPlan.TargetServiceName.Should().Be("coastal");
        publishPlan.TargetLayerName.Should().Be("states");
        publishPlan.ConnectionAlias.Should().Be("demo:states");
        publishPlan.EligibleForDirectPublish.Should().BeTrue();
        publishPlan.Status.Should().Be(MigrationPlanStatus.Ready);

        var schemaRegistry = new MetadataSchemaRegistry();
        manifest1.MetadataResources.Should().HaveCount(2);
        foreach (var resource in manifest1.MetadataResources)
        {
            schemaRegistry.ValidateAndUpgrade(resource).IsValid.Should().BeTrue();
        }

        manifest1.StylePlan.Should().ContainSingle();
        var stylePlan = manifest1.StylePlan.Single();
        stylePlan.TranslationStatus.Should().Be(MigrationStyleTranslationStatus.ManualActionRequired);
        stylePlan.DiagnosticCodes.Should().Contain(MigrationReasonCodes.UnsupportedSldStyle);
        stylePlan.SourceContent.Should().Contain("StyledLayerDescriptor");

        var manifestJson = JsonSerializer.Serialize(manifest1, MigrationManifestJsonContext.Default.MigrationManifest);
        manifestJson.Should().NotContain("db-password-123");
        manifestJson.Should().NotContain("geoserver-password-123");
    }

    [Fact]
    public async Task TranslateAsync_WithUnsupportedResources_EmitsExplicitDiagnostics()
    {
        var serviceInfo = new GeoServerServiceInfo
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            Workspaces =
            [
                new GeoServerWorkspaceInfo
                {
                    Name = "demo"
                }
            ],
            DataStores =
            [
                new GeoServerDataStoreInfo
                {
                    Name = "roads-store",
                    WorkspaceName = "demo",
                    Type = "Shapefile",
                    ConnectionParameters = new Dictionary<string, object>()
                }
            ],
            CoverageStores =
            [
                new GeoServerCoverageStoreInfo
                {
                    Name = "imagery",
                    WorkspaceName = "demo",
                    Type = "GeoTIFF"
                }
            ],
            Layers =
            [
                new GeoServerLayerInfo
                {
                    Name = "roads",
                    WorkspaceName = "demo",
                    DataStoreName = "roads-store",
                    NativeName = "roads",
                    SRS = "EPSG:4326",
                    GeometryColumn = "the_geom",
                    GeometryType = "LineString",
                    DefaultStyle = "roads-style"
                },
                new GeoServerLayerInfo
                {
                    Name = "ortho",
                    WorkspaceName = "demo",
                    CoverageStoreName = "imagery",
                    DefaultStyle = "roads-style"
                }
            ],
            LayerGroups =
            [
                new GeoServerLayerGroupInfo
                {
                    Name = "bundle",
                    WorkspaceName = "demo",
                    Layers =
                    [
                        new GeoServerLayerGroupEntry
                        {
                            Name = "demo:roads",
                            Type = "LAYER"
                        }
                    ]
                }
            ],
            Styles =
            [
                new GeoServerStyleInfo
                {
                    Name = "roads-style",
                    Format = "css"
                }
            ]
        };

        var sut = new GeoServerMigrationManifestService(
            new StubGeoServerImportService(serviceInfo),
            NullLogger<GeoServerMigrationManifestService>.Instance);

        var manifest = await sut.TranslateAsync(new GeoServerTranslationRequest
        {
            GeoServerRestUrl = serviceInfo.GeoServerRestUrl,
            ImportStyles = true
        });

        manifest.ConnectionDrafts.Should().BeEmpty();
        manifest.PublishPlan.Should().BeEmpty();
        manifest.StylePlan.Should().HaveCount(2);
        manifest.StylePlan.Should().OnlyContain(entry =>
            entry.TranslationStatus == MigrationStyleTranslationStatus.Unsupported &&
            entry.DiagnosticCodes.Contains(MigrationReasonCodes.UnsupportedStyleFormat));
        manifest.StylePlan.Should().OnlyContain(entry =>
            entry.SourceReferenceUrl == "https://example.com/geoserver/rest/styles/roads-style.css");

        manifest.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain(
        [
            MigrationReasonCodes.UnsupportedDatastoreType,
            MigrationReasonCodes.UnsupportedCoverageStore,
            MigrationReasonCodes.UnsupportedLayerSource,
            MigrationReasonCodes.UnsupportedLayerGroup,
            MigrationReasonCodes.UnsupportedStyleFormat
        ]);
    }

    [Fact]
    public async Task TranslateAsync_WithCrsAndSchemaGaps_EmitsManualPublishDiagnostics()
    {
        var serviceInfo = new GeoServerServiceInfo
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            Workspaces =
            [
                new GeoServerWorkspaceInfo
                {
                    Name = "demo"
                }
            ],
            DataStores =
            [
                new GeoServerDataStoreInfo
                {
                    Name = "postgis",
                    WorkspaceName = "demo",
                    Type = "PostGIS",
                    ConnectionParameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["host"] = "db.internal",
                        ["database"] = "gis"
                    }
                }
            ],
            Layers =
            [
                new GeoServerLayerInfo
                {
                    Name = "roads",
                    WorkspaceName = "demo",
                    DataStoreName = "postgis",
                    NativeName = "roads",
                    GeometryColumn = "geom"
                },
                new GeoServerLayerInfo
                {
                    Name = "buildings",
                    WorkspaceName = "demo",
                    DataStoreName = "postgis",
                    NativeName = "public.buildings",
                    GeometryColumn = "geom",
                    GeometryType = "Polygon",
                    SRS = "EPSG:4326"
                }
            ]
        };

        var sut = new GeoServerMigrationManifestService(
            new StubGeoServerImportService(serviceInfo),
            NullLogger<GeoServerMigrationManifestService>.Instance);

        var manifest = await sut.TranslateAsync(new GeoServerTranslationRequest
        {
            GeoServerRestUrl = serviceInfo.GeoServerRestUrl,
            TargetSrid = 3857
        });

        manifest.PublishPlan.Should().HaveCount(2);
        manifest.PublishPlan.Should().OnlyContain(plan => plan.Status == MigrationPlanStatus.ManualActionRequired);
        manifest.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain(
        [
            MigrationReasonCodes.ResolveSourceSchema,
            MigrationReasonCodes.MissingGeometryType,
            MigrationReasonCodes.ResolveAmbiguousSrid,
            MigrationReasonCodes.UnsupportedTargetSridTransform
        ]);
    }

    [Fact]
    public async Task TranslateAsync_WithConflictingTargetLayerNames_RequiresManualReplay()
    {
        var serviceInfo = new GeoServerServiceInfo
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            Workspaces =
            [
                new GeoServerWorkspaceInfo
                {
                    Name = "north"
                },
                new GeoServerWorkspaceInfo
                {
                    Name = "south"
                }
            ],
            DataStores =
            [
                new GeoServerDataStoreInfo
                {
                    Name = "parcels",
                    WorkspaceName = "north",
                    Type = "PostGIS",
                    ConnectionParameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["host"] = "db.internal",
                        ["database"] = "gis",
                        ["schema"] = "public"
                    }
                },
                new GeoServerDataStoreInfo
                {
                    Name = "parcels",
                    WorkspaceName = "south",
                    Type = "PostGIS",
                    ConnectionParameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["host"] = "db.internal",
                        ["database"] = "gis",
                        ["schema"] = "public"
                    }
                }
            ],
            Layers =
            [
                new GeoServerLayerInfo
                {
                    Name = "parcels",
                    WorkspaceName = "north",
                    DataStoreName = "parcels",
                    NativeName = "public.parcels_north",
                    GeometryColumn = "geom",
                    GeometryType = "Polygon",
                    SRS = "EPSG:4326"
                },
                new GeoServerLayerInfo
                {
                    Name = "parcels",
                    WorkspaceName = "south",
                    DataStoreName = "parcels",
                    NativeName = "public.parcels_south",
                    GeometryColumn = "geom",
                    GeometryType = "Polygon",
                    SRS = "EPSG:4326"
                }
            ]
        };

        var sut = new GeoServerMigrationManifestService(
            new StubGeoServerImportService(serviceInfo),
            NullLogger<GeoServerMigrationManifestService>.Instance);

        var manifest = await sut.TranslateAsync(new GeoServerTranslationRequest
        {
            GeoServerRestUrl = serviceInfo.GeoServerRestUrl,
            ImportOptions = new GeoServerImportOptions
            {
                WorkspaceNameMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["north"] = "coastal",
                    ["south"] = "coastal"
                }
            }
        });

        manifest.PublishPlan.Should().HaveCount(2);
        manifest.PublishPlan.Should().OnlyContain(plan =>
            plan.Status == MigrationPlanStatus.ManualActionRequired &&
            plan.EligibleForDirectPublish == false &&
            plan.DiagnosticCodes.Contains(MigrationReasonCodes.ConflictingTargetLayerName));
        manifest.MetadataResources.Should().ContainSingle(resource => resource.Kind == MetadataResourceKinds.Service);
        manifest.MetadataResources.Should().NotContain(resource => resource.Kind == MetadataResourceKinds.Layer);
        manifest.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain(MigrationReasonCodes.ConflictingTargetLayerName);
    }

    private sealed class StubGeoServerImportService(GeoServerServiceInfo serviceInfo) : IGeoServerImportService
    {
        public Task<GeoServerServiceInfo> DiscoverServiceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(serviceInfo);

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            IProgress<GeoServerImportProgress>? progress,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
