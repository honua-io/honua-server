// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Styling.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for packaging domain models.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class PackageDomainTests
{
    [UnitTest]
    [Operation(Operations.Create)]
    public void MapPackage_CreateDraft_ProducesCorrectInitialState()
    {
        var bindings = new[]
        {
            new SourceBinding
            {
                SourceId = "src-1",
                Protocol = SourceProtocol.OgcFeatures,
                Locator = new SourceLocator { Url = "https://example.com/ogc" }
            }
        };

        var pkg = MapPackage.CreateDraft(
            "map-pkg-1",
            "honua_map_package.v1",
            bindings,
            [new StyleRef { StyleId = "style-1" }]);

        pkg.MapPackageId.Should().Be("map-pkg-1");
        pkg.Format.Should().Be("honua_map_package.v1");
        pkg.Status.Should().Be(PackageStatus.Draft);
        pkg.SourceBindings.Should().HaveCount(1);
        pkg.StyleRefs.Should().ContainSingle().Which.StyleId.Should().Be("style-1");
        pkg.TemplateId.Should().BeNull();
        pkg.ThemeId.Should().BeNull();
        pkg.Legend.Should().BeEmpty();
        pkg.PopupBindings.Should().BeEmpty();
        pkg.LabelBindings.Should().BeEmpty();
        pkg.BoundArtifacts.Should().BeEmpty();
        pkg.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        pkg.UpdatedAt.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void MapPackage_CreateDraft_WithOptionalParams_SetsTemplateAndTheme()
    {
        var pkg = MapPackage.CreateDraft(
            "map-pkg-2",
            "honua_map_package.v1",
            Array.Empty<SourceBinding>(),
            Array.Empty<StyleRef>(),
            templateId: "tmpl-1",
            themeId: "theme-1");

        pkg.TemplateId.Should().Be("tmpl-1");
        pkg.ThemeId.Should().Be("theme-1");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void AppPackage_CreateDraft_ProducesCorrectInitialState()
    {
        var pkg = AppPackage.CreateDraft(
            "app-pkg-1",
            "honua-sdk-js",
            "honua_app_package.v1");

        pkg.AppPackageId.Should().Be("app-pkg-1");
        pkg.TargetSdk.Should().Be("honua-sdk-js");
        pkg.Format.Should().Be("honua_app_package.v1");
        pkg.Status.Should().Be(PackageStatus.Draft);
        pkg.TemplateId.Should().BeNull();
        pkg.EntryPoint.Should().BeNull();
        pkg.GeneratedFiles.Should().BeEmpty();
        pkg.BundleArtifactId.Should().BeNull();
        pkg.AssetManifest.Should().BeEmpty();
        pkg.MapPackageId.Should().BeNull();
        pkg.RuntimeConfigSchema.Should().BeNull();
        pkg.DeliveryHints.Should().BeNull();
        pkg.BoundArtifacts.Should().BeEmpty();
        pkg.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        pkg.UpdatedAt.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void AppPackage_CreateDraft_WithOptionalParams_SetsTemplateAndMap()
    {
        var pkg = AppPackage.CreateDraft(
            "app-pkg-2",
            "honua-sdk-js",
            "honua_app_package.v1",
            templateId: "tmpl-1",
            mapPackageId: "map-pkg-1");

        pkg.TemplateId.Should().Be("tmpl-1");
        pkg.MapPackageId.Should().Be("map-pkg-1");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void MapPackage_JsonRoundTrip_PreservesAllFields()
    {
        var original = new MapPackage
        {
            MapPackageId = "map-rt-1",
            Format = "honua_map_package.v1",
            Status = PackageStatus.Ready,
            TemplateId = "tmpl-1",
            SourceBindings =
            [
                new SourceBinding
                {
                    SourceId = "src-1",
                    Protocol = SourceProtocol.VectorTile,
                    Locator = new SourceLocator
                    {
                        Url = "https://tiles.example.com",
                        ServiceId = "svc-1",
                        LayerId = "layer-0"
                    },
                    Filter = "population > 1000",
                    Metadata = new Dictionary<string, string> { ["origin"] = "census" }
                }
            ],
            StyleRefs =
            [
                new StyleRef { StyleId = "style-a", Label = "Style A" },
                new StyleRef { StyleId = "style-b", PresetId = "preset-1" }
            ],
            ThemeId = "theme-dark",
            InitialView = new MapInitialView
            {
                Bbox = [-122.5, 37.5, -122.0, 38.0],
                Crs = "EPSG:4326"
            },
            Legend =
            [
                new LegendEntry
                {
                    Label = "Low",
                    Color = "#2D69A5",
                    MinValue = 0,
                    MaxValue = 100
                }
            ],
            PopupBindings =
            [
                new PopupBinding { SourceId = "src-1", FieldName = "name", Template = "<b>{name}</b>" }
            ],
            LabelBindings =
            [
                new LabelBinding { SourceId = "src-1", FieldName = "city", Placement = "top" }
            ],
            PreviewArtifactId = "artifact-preview-1",
            BoundArtifacts = ["artifact-1", "artifact-2"],
            CreatedAt = new DateTimeOffset(2026, 4, 14, 12, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 4, 14, 13, 0, 0, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(original, PackagingJsonContext.Default.MapPackage);
        var deserialized = JsonSerializer.Deserialize(json, PackagingJsonContext.Default.MapPackage);

        deserialized.Should().NotBeNull();
        deserialized!.MapPackageId.Should().Be(original.MapPackageId);
        deserialized.Format.Should().Be(original.Format);
        deserialized.Status.Should().Be(PackageStatus.Ready);
        deserialized.TemplateId.Should().Be("tmpl-1");
        deserialized.SourceBindings.Should().HaveCount(1);
        deserialized.SourceBindings[0].SourceId.Should().Be("src-1");
        deserialized.SourceBindings[0].Protocol.Should().Be(SourceProtocol.VectorTile);
        deserialized.SourceBindings[0].Locator.Url.Should().Be("https://tiles.example.com");
        deserialized.SourceBindings[0].Locator.ServiceId.Should().Be("svc-1");
        deserialized.SourceBindings[0].Locator.LayerId.Should().Be("layer-0");
        deserialized.SourceBindings[0].Filter.Should().Be("population > 1000");
        deserialized.StyleRefs.Should().HaveCount(2);
        deserialized.StyleRefs[0].StyleId.Should().Be("style-a");
        deserialized.StyleRefs[0].Label.Should().Be("Style A");
        deserialized.StyleRefs[1].StyleId.Should().Be("style-b");
        deserialized.StyleRefs[1].PresetId.Should().Be("preset-1");
        deserialized.ThemeId.Should().Be("theme-dark");
        deserialized.InitialView.Should().NotBeNull();
        deserialized.InitialView!.Bbox.Should().BeEquivalentTo(new[] { -122.5, 37.5, -122.0, 38.0 });
        deserialized.InitialView.Crs.Should().Be("EPSG:4326");
        deserialized.Legend.Should().HaveCount(1);
        deserialized.Legend[0].Label.Should().Be("Low");
        deserialized.Legend[0].Color.Should().Be("#2D69A5");
        deserialized.PopupBindings.Should().HaveCount(1);
        deserialized.LabelBindings.Should().HaveCount(1);
        deserialized.PreviewArtifactId.Should().Be("artifact-preview-1");
        deserialized.BoundArtifacts.Should().HaveCount(2);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);
        deserialized.UpdatedAt.Should().Be(original.UpdatedAt);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AppPackage_JsonRoundTrip_PreservesAllFields()
    {
        var original = new AppPackage
        {
            AppPackageId = "app-rt-1",
            TargetSdk = "honua-sdk-js",
            Format = "honua_app_package.v1",
            Status = PackageStatus.Composing,
            TemplateId = "tmpl-2",
            EntryPoint = "src/main.ts",
            GeneratedFiles = ["src/main.ts", "src/config.json"],
            BundleArtifactId = "artifact-bundle-1",
            AssetManifest =
            [
                new AssetManifestEntry { Path = "index.html", ContentType = "text/html" },
                new AssetManifestEntry { Path = "app.js", ContentType = "application/javascript" }
            ],
            MapPackageId = "map-pkg-1",
            DeliveryHints = new DeliveryHints
            {
                HostingMode = "static_site",
                DefaultRoutePrefix = "/apps/mini-1"
            },
            BoundArtifacts = ["artifact-bundle-1"],
            CreatedAt = new DateTimeOffset(2026, 4, 14, 12, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 4, 14, 14, 0, 0, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(original, PackagingJsonContext.Default.AppPackage);
        var deserialized = JsonSerializer.Deserialize(json, PackagingJsonContext.Default.AppPackage);

        deserialized.Should().NotBeNull();
        deserialized!.AppPackageId.Should().Be(original.AppPackageId);
        deserialized.TargetSdk.Should().Be("honua-sdk-js");
        deserialized.Format.Should().Be("honua_app_package.v1");
        deserialized.Status.Should().Be(PackageStatus.Composing);
        deserialized.EntryPoint.Should().Be("src/main.ts");
        deserialized.GeneratedFiles.Should().HaveCount(2);
        deserialized.BundleArtifactId.Should().Be("artifact-bundle-1");
        deserialized.AssetManifest.Should().HaveCount(2);
        deserialized.AssetManifest[0].Path.Should().Be("index.html");
        deserialized.AssetManifest[0].ContentType.Should().Be("text/html");
        deserialized.MapPackageId.Should().Be("map-pkg-1");
        deserialized.DeliveryHints.Should().NotBeNull();
        deserialized.DeliveryHints!.HostingMode.Should().Be("static_site");
        deserialized.DeliveryHints.DefaultRoutePrefix.Should().Be("/apps/mini-1");
        deserialized.BoundArtifacts.Should().ContainSingle();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void PackageStatus_AllValuesAreDefined()
    {
        var values = Enum.GetValues<PackageStatus>();
        values.Should().HaveCount(5);
        values.Should().Contain(PackageStatus.Draft);
        values.Should().Contain(PackageStatus.Composing);
        values.Should().Contain(PackageStatus.Ready);
        values.Should().Contain(PackageStatus.Failed);
        values.Should().Contain(PackageStatus.Expired);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void SourceProtocol_AllValuesAreDefined()
    {
        var values = Enum.GetValues<SourceProtocol>();
        values.Should().HaveCount(12);
        values.Should().Contain(SourceProtocol.GeoservicesFeatureService);
        values.Should().Contain(SourceProtocol.OgcFeatures);
        values.Should().Contain(SourceProtocol.VectorTile);
        values.Should().Contain(SourceProtocol.WorkspaceArtifact);
        values.Should().Contain(SourceProtocol.PMTiles);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SourceBinding_RequiredFields_ConstructsCorrectly()
    {
        var binding = new SourceBinding
        {
            SourceId = "src-test",
            Protocol = SourceProtocol.Wms,
            Locator = new SourceLocator { Url = "https://wms.example.com/service" }
        };

        binding.SourceId.Should().Be("src-test");
        binding.Protocol.Should().Be(SourceProtocol.Wms);
        binding.Locator.Url.Should().Be("https://wms.example.com/service");
        binding.Locator.ServiceId.Should().BeNull();
        binding.Locator.LayerId.Should().BeNull();
        binding.Filter.Should().BeNull();
        binding.Metadata.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void MapInitialView_Bbox_StoresCoordinatesCorrectly()
    {
        var view = new MapInitialView
        {
            Bbox = [-180.0, -90.0, 180.0, 90.0],
            Crs = "EPSG:4326"
        };

        view.Bbox.Should().HaveCount(4);
        view.Bbox[0].Should().Be(-180.0);
        view.Bbox[1].Should().Be(-90.0);
        view.Bbox[2].Should().Be(180.0);
        view.Bbox[3].Should().Be(90.0);
        view.Crs.Should().Be("EPSG:4326");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void AnalysisResultPackage_CreateCompleted_WithPackageRefs_PopulatesIds()
    {
        var provenance = new ProvenanceRecord
        {
            Sources = [new ProvenanceSource { SourceId = "src-1" }],
            ProcessDefinitions = ["buffer"]
        };
        var summary = new ResultSummary { Title = "Buffer analysis" };

        var result = AnalysisResultPackage.CreateCompleted(
            "rp-1",
            summary,
            Array.Empty<ArtifactRef>(),
            Array.Empty<WorkspaceRef>(),
            provenance,
            mapPackageId: "map-pkg-1",
            appPackageId: "app-pkg-1");

        result.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
        result.MapPackageId.Should().Be("map-pkg-1");
        result.AppPackageId.Should().Be("app-pkg-1");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void AnalysisResultPackage_CreateCompleted_WithoutPackageRefs_LeavesIdsNull()
    {
        var provenance = new ProvenanceRecord
        {
            Sources = [new ProvenanceSource { SourceId = "src-1" }],
            ProcessDefinitions = ["intersect"]
        };
        var summary = new ResultSummary { Title = "Intersect analysis" };

        var result = AnalysisResultPackage.CreateCompleted(
            "rp-2",
            summary,
            Array.Empty<ArtifactRef>(),
            Array.Empty<WorkspaceRef>(),
            provenance);

        result.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
        result.MapPackageId.Should().BeNull();
        result.AppPackageId.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void AnalysisResultPackage_CreateFailed_PackageRefsAreNull()
    {
        var provenance = new ProvenanceRecord
        {
            Sources = [new ProvenanceSource { SourceId = "src-1" }],
            ProcessDefinitions = ["clip"]
        };
        var summary = new ResultSummary { Title = "Clip failed" };
        var errors = new[]
        {
            new GeoprocessingError
            {
                Kind = GeoprocessingErrorKind.ExecutionFailed,
                Message = "Execution failed"
            }
        };

        var result = AnalysisResultPackage.CreateFailed("rp-3", summary, errors, provenance);

        result.Status.Should().Be(GeoprocessingWorkflowStatus.Failed);
        result.MapPackageId.Should().BeNull();
        result.AppPackageId.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void PackageStatus_JsonRoundTrip_SerializesAsString()
    {
        var pkg = MapPackage.CreateDraft("status-test", "honua_map_package.v1", Array.Empty<SourceBinding>(), Array.Empty<StyleRef>());
        var json = JsonSerializer.Serialize(pkg, PackagingJsonContext.Default.MapPackage);

        json.Should().Contain("\"status\":\"Draft\"");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void SourceProtocol_JsonRoundTrip_SerializesAsString()
    {
        var binding = new SourceBinding
        {
            SourceId = "proto-test",
            Protocol = SourceProtocol.OgcFeatures,
            Locator = new SourceLocator { Url = "https://example.com" }
        };

        var json = JsonSerializer.Serialize(binding, PackagingJsonContext.Default.SourceBinding);
        json.Should().Contain("\"protocol\":\"ogc_features\"");
    }
}
