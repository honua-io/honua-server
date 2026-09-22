// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Xunit;

namespace Honua.Core.Tests.Features.Migration;

/// <summary>
/// Issue #4600, acceptance criterion 1: every construct discovered on the source is accounted against the
/// service migration selection before apply, and unselected resources, unsupported service types and
/// unsupported constructs block full fidelity. Expected entries and differences are derived by hand from
/// each manifest below, not captured from the accountant's output.
/// </summary>
public sealed class MigrationServiceConstructAccountantTests
{
    private const string ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer";
    private const string ServiceId = "service:Inspections";
    private const string Layer = "resource:Inspections:layer:0";
    private const string Table = "resource:Inspections:table:1";
    private const string Renderer = "renderer:Inspections:layer:0";

    [Fact]
    public void Account_FullSelectionOfAutomatedConstructs_AccountsEveryConstructWithNoDifference()
    {
        var report = MigrationServiceConstructAccountant.Account(AutomatedManifest(), FullSelection());

        report.Executed.Should().BeTrue();
        report.MatrixVersion.Should().Be(MigrationConstructMatrix.Version);
        report.DiscoveredResourceCount.Should().Be(2);
        report.SelectedResourceCount.Should().Be(2);
        report.Differences.Should().BeEmpty();
        report.IsBlocking.Should().BeFalse();

        // 3 service constructs, 8 for the point layer (its geometry included), 4 for the table (no geometry).
        report.Entries.Select(entry => (entry.SourceId, entry.Construct)).Should().Equal(
            (Layer, "resource.attachments"),
            (Layer, "resource.capabilities"),
            (Layer, "resource.domains"),
            (Layer, "resource.fields"),
            (Layer, "resource.geometry"),
            (Layer, "resource.identity"),
            (Layer, "resource.relationships"),
            (Layer, "service.resource"),
            (Table, "resource.capabilities"),
            (Table, "resource.fields"),
            (Table, "resource.identity"),
            (Table, "service.resource"),
            (ServiceId, "service.capabilities"),
            (ServiceId, "service.identity"),
            (ServiceId, "service.type"));
        report.Entries.Should().OnlyContain(entry => entry.Disposition == MigrationConstructDispositions.Migrated);

        var layerResource = report.Entries.Single(entry => entry.SourceId == Layer && entry.Construct == "service.resource");
        layerResource.TargetResourceId.Should().Be("target:layer0");
        layerResource.TargetTable.Should().Be("field_ops.inspections");
        layerResource.Verification.Should().Be(MigrationConstructVerifications.DataReconciliation);
        report.Entries.Single(entry => entry.SourceId == Layer && entry.Construct == "resource.geometry")
            .Codes.Should().Equal("esriGeometryPoint");
        report.Entries.Single(entry => entry.SourceId == Layer && entry.Construct == "resource.attachments")
            .Verification.Should().Be(MigrationConstructVerifications.AttachmentReconciliation);
        report.Entries.Single(entry => entry.SourceId == Layer && entry.Construct == "resource.relationships")
            .Verification.Should().Be(MigrationConstructVerifications.RelationshipApply);
        report.Entries.Single(entry => entry.Construct == "service.type").Codes.Should().Equal("FeatureServer");
    }

    [Fact]
    public void Account_DiscoveredTableLeftOutOfTheSelection_BlocksWithTheUnselectedTable()
    {
        var report = MigrationServiceConstructAccountant.Account(AutomatedManifest(), [Select(Layer, "field_ops.inspections")]);

        report.IsBlocking.Should().BeTrue();
        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ServiceResourceUnselected);
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
        difference.Subject.Should().Be(Table);
        difference.Actual.Should().Be("not selected");

        report.Entries.Where(entry => entry.SourceId == Table).Should().HaveCount(4)
            .And.OnlyContain(entry => entry.Disposition == MigrationConstructDispositions.Unselected && entry.TargetTable == null);
        report.Entries.Where(entry => entry.SourceId != Table)
            .Should().OnlyContain(entry => entry.Disposition == MigrationConstructDispositions.Migrated);
    }

    [Fact]
    public void Account_SelectedLayerWithUnsupportedGeometry_BlocksOnceWithTheSourceCode()
    {
        const string curves = "resource:Inspections:layer:2";
        var manifest = AutomatedManifest() with
        {
            // The translator emits no target resource for an incompatible layer; only the unsupported item.
            UnsupportedItems =
            [
                new MigrationManifestReviewItem
                {
                    SourceId = curves,
                    Kind = "layer",
                    Code = ImportCompatibilityCodes.ArcGisUnsupportedGeometry,
                    Severity = "unsupported",
                    Reason = "Geometry type 'esriGeometryMultiPatch' is not supported by the current import path."
                }
            ]
        };

        var report = MigrationServiceConstructAccountant.Account(manifest, [.. FullSelection(), Select(curves, "field_ops.curves")]);

        report.DiscoveredResourceCount.Should().Be(3);
        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ConstructUnsupported);
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
        difference.Subject.Should().Be(curves);
        difference.Actual.Should().Be("unsupported (ARCGIS_UNSUPPORTED_GEOMETRY)");
        report.Entries.Where(entry => entry.SourceId == curves)
            .Select(entry => (entry.Construct, entry.Disposition, entry.TargetTable))
            .Should().Equal(
                ("resource.geometry", MigrationConstructDispositions.Blocker, "field_ops.curves"),
                ("service.resource", MigrationConstructDispositions.Blocker, "field_ops.curves"));

        var unselected = MigrationServiceConstructAccountant.Account(manifest, FullSelection());
        unselected.Differences.Select(entry => (entry.Code, entry.Subject)).Should().Equal(
            (MigrationFidelityDifferenceCodes.ServiceResourceUnselected, curves));
    }

    [Fact]
    public void Account_LayerWithoutSpatialReference_ReviewsItsGeometry()
    {
        var manifest = AutomatedManifest() with
        {
            TargetResources =
            [
                Target(Layer, "layer", "target:layer0", "esriGeometryPoint", "partial", ImportCompatibilityCodes.ArcGisMissingSpatialRef),
                Target(Table, "table", "target:table1")
            ]
        };

        var report = MigrationServiceConstructAccountant.Account(manifest, FullSelection());

        report.IsBlocking.Should().BeFalse();
        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ConstructReviewRequired);
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Unverified);
        difference.Subject.Should().Be(Layer);
        difference.Expected.Should().Be("resource.geometry migrated automatically");
        difference.Actual.Should().Be("manual-review (ARCGIS_MISSING_SPATIAL_REF)");
    }

    [Theory]
    [InlineData("ImageServer", "https://example.com/arcgis/rest/services/Elevation/ImageServer", "ImageServer")]
    [InlineData(null, "https://example.com/arcgis/rest/services/Locators/GeocodeServer", "GeocodeServer")]
    [InlineData(null, "https://example.com/", "service type not advertised")]
    public void Account_UnsupportedServiceType_Blocks(string? serviceType, string baseUrl, string expectedActual)
    {
        var manifest = AutomatedManifest() with
        {
            Source = new MigrationSourceIdentity { DisplayName = "Source", BaseUrl = baseUrl, ServiceType = serviceType }
        };

        var report = MigrationServiceConstructAccountant.Account(manifest, FullSelection());

        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ServiceTypeUnsupported);
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
        difference.Subject.Should().Be(ServiceId);
        difference.Actual.Should().Be(expectedActual);
        report.Entries.Single(entry => entry.Construct == "service.type").Disposition
            .Should().Be(MigrationConstructDispositions.Blocker);
    }

    [Fact]
    public void Account_MapServerFromItsUrl_IsSupported()
    {
        var manifest = AutomatedManifest() with
        {
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Inspections",
                BaseUrl = "https://example.com/arcgis/rest/services/Inspections/MapServer"
            }
        };

        MigrationServiceConstructAccountant.Account(manifest, FullSelection()).Differences.Should().BeEmpty();
    }

    [Fact]
    public void Account_SelectionTheManifestNeverDiscovered_BlocksWithoutInventingConstructs()
    {
        const string other = "resource:Other:layer:9";

        var report = MigrationServiceConstructAccountant.Account(AutomatedManifest(), [.. FullSelection(), Select(other, "other_9")]);

        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ServiceResourceUndiscovered);
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
        difference.Subject.Should().Be(other);
        var entry = report.Entries.Where(item => item.SourceId == other).Should().ContainSingle().Subject;
        entry.Construct.Should().Be("service.resource");
        entry.Disposition.Should().Be(MigrationConstructDispositions.Undiscovered);
        entry.TargetTable.Should().Be("other_9");
        report.SelectedResourceCount.Should().Be(3);
    }

    [Fact]
    public void Account_ReviewedConstructs_AreUnverifiedPerConstructAndFollowTheirOwningLayer()
    {
        var manifest = AutomatedManifest() with
        {
            StyleActions =
            [
                new MigrationManifestStyleAction
                {
                    SourceStyleId = Renderer,
                    TargetStyleId = "style:inspections",
                    Action = "manual-review",
                    ResourceIds = [Layer],
                    Compatibility = Compatibility("partial", ImportCompatibilityCodes.ManualReview)
                }
            ],
            FidelityMatrix = new MigrationFidelityMatrix
            {
                Cells =
                [
                    .. AutomatedCells(),
                    Cell("relationships", MigrationFidelityAutomationStatuses.ManualReview, [ImportCompatibilityCodes.ArcGisRelationshipsManualReview], Layer),
                    Cell("subtypes", MigrationFidelityAutomationStatuses.ManualReview, [ImportCompatibilityCodes.ArcGisSubtypesManualReview], Layer),
                    Cell("renderers", MigrationFidelityAutomationStatuses.Assisted, [ImportCompatibilityCodes.Compatible], Renderer)
                ]
            }
        };

        var report = MigrationServiceConstructAccountant.Account(manifest, FullSelection());

        report.IsBlocking.Should().BeFalse();
        report.Differences.Should().OnlyContain(difference =>
            difference.Code == MigrationFidelityDifferenceCodes.ConstructReviewRequired &&
            difference.Severity == MigrationFidelityDifferenceSeverities.Unverified);
        report.Differences.Select(difference => (difference.Subject, difference.Expected, difference.Actual))
            .Should().BeEquivalentTo(new[]
            {
                (Renderer, "resource.styles migrated automatically", "assisted (COMPATIBLE)"),
                (Layer, "resource.relationships migrated automatically", "manual-review (ARCGIS_RELATIONSHIPS_MANUAL_REVIEW)"),
                (Layer, "resource.subtypes migrated automatically", "manual-review (ARCGIS_SUBTYPES_MANUAL_REVIEW)")
            });

        // The simple relationship stays migrated alongside the one that needs review.
        report.Entries.Where(entry => entry.SourceId == Layer && entry.Construct == "resource.relationships")
            .Select(entry => entry.Disposition)
            .Should().BeEquivalentTo([MigrationConstructDispositions.Migrated, MigrationConstructDispositions.Review]);
        var style = report.Entries.Single(entry => entry.SourceId == Renderer);
        style.ResourceIds.Should().Equal(Layer);
        style.TargetResourceId.Should().Be("target:layer0");
        style.Verification.Should().Be(MigrationConstructVerifications.OperatorReview);

        // Leaving the owning layer out turns its reviewed constructs into unselected ones, not review items.
        var tableOnly = MigrationServiceConstructAccountant.Account(manifest, [Select(Table, "field_ops.inspection_notes")]);
        tableOnly.Differences.Select(difference => (difference.Code, difference.Subject)).Should().Equal(
            (MigrationFidelityDifferenceCodes.ServiceResourceUnselected, Layer));
        tableOnly.Entries.Single(entry => entry.SourceId == Renderer).Disposition
            .Should().Be(MigrationConstructDispositions.Unselected);
    }

    [Fact]
    public void Account_CategoryWithoutAMatrixRow_IsAccountedForReviewNotDropped()
    {
        var manifest = AutomatedManifest() with
        {
            FidelityMatrix = new MigrationFidelityMatrix
            {
                Cells = [.. AutomatedCells(), Cell("cartography", MigrationFidelityAutomationStatuses.Automated, [ImportCompatibilityCodes.Compatible], Layer)]
            }
        };

        var report = MigrationServiceConstructAccountant.Account(manifest, FullSelection());

        var entry = report.Entries.Single(item => item.Construct == MigrationConstructKeys.Unmapped);
        entry.SourceId.Should().Be(Layer);
        entry.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        entry.Codes.Should().Equal("COMPATIBLE", "category:cartography");
        entry.Disposition.Should().Be(MigrationConstructDispositions.Review);
        report.Differences.Should().ContainSingle().Which.Code.Should().Be(MigrationFidelityDifferenceCodes.ConstructReviewRequired);
    }

    [Fact]
    public void Account_EditableSourceLayer_ReviewsItsPublishedEditBehavior()
    {
        var manifest = AutomatedManifest() with
        {
            TargetResources =
            [
                Target(Layer, "layer", "target:layer0", "esriGeometryPoint", capabilities: ["Create", "Query", "Update"]),
                Target(Table, "table", "target:table1")
            ]
        };

        var report = MigrationServiceConstructAccountant.Account(manifest, FullSelection());

        var entry = report.Entries.Single(item => item.Construct == "resource.edit-behavior");
        entry.SourceId.Should().Be(Layer);
        entry.Codes.Should().Equal("Create", "Update");
        entry.Disposition.Should().Be(MigrationConstructDispositions.Review);
        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ConstructReviewRequired);
        difference.Actual.Should().Be("manual-review (Create, Update)");
    }

    [Fact]
    public void Account_UnsupportedLabelClass_BlocksWithTheLabelClass()
    {
        var manifest = AutomatedManifest() with
        {
            TargetResources =
            [
                Target(Layer, "layer", "target:layer0", "esriGeometryPoint") with
                {
                    LabelClassDiagnostics =
                    [
                        new MigrationManifestLabelClassDiagnostic
                        {
                            SourceStyleId = Renderer,
                            SourceResourceId = Layer,
                            LabelClassIndex = 0,
                            ExpressionEngine = "VBScript",
                            Classification = MigrationManifestLabelClassClassifications.Unsupported
                        }
                    ]
                },
                Target(Table, "table", "target:table1")
            ]
        };

        var report = MigrationServiceConstructAccountant.Account(manifest, FullSelection());

        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ConstructUnsupported);
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
        difference.Subject.Should().Be(Renderer + ":label-class:0");
        difference.Expected.Should().Be("resource.styles migrated automatically");
        difference.Actual.Should().Be("unsupported (expression-engine:VBScript)");
    }

    [Theory]
    [InlineData("arcgis-geoservices-rest", null, "no source manifest was supplied")]
    [InlineData("arcgis-geoservices-rest", "{ not json", "the source manifest could not be parsed")]
    [InlineData("ogc-wms", "valid", "no construct matrix is defined for source kind 'ogc-wms'")]
    public void Account_WithoutAReadableArcGisManifest_DoesNotExecuteAndCannotProveFullFidelity(
        string sourceKind,
        string? manifestBody,
        string expectedReason)
    {
        var body = manifestBody == "valid" ? Serialize(AutomatedManifest()) : manifestBody;

        var report = MigrationServiceConstructAccountant.Account(sourceKind, body, FullSelection());

        report.Executed.Should().BeFalse();
        report.NotExecutedReason.Should().StartWith(expectedReason);
        report.SelectedResourceCount.Should().Be(2);
        report.Entries.Should().BeEmpty();
        report.IsBlocking.Should().BeFalse();
        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Code.Should().Be(MigrationFidelityDifferenceCodes.ConstructAccountingNotExecuted);
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Unverified);
        difference.Summary.Should().Contain(expectedReason);
    }

    [Fact]
    public void Account_PersistedManifestBody_ReplaysTheSameAccounting()
    {
        var manifest = AutomatedManifest();
        var selection = new[] { Select(Layer, "field_ops.inspections") };

        var fromBody = MigrationServiceConstructAccountant.Account("arcgis-geoservices-rest", Serialize(manifest), selection);

        fromBody.Should().BeEquivalentTo(MigrationServiceConstructAccountant.Account(manifest, selection));
        fromBody.Differences.Should().ContainSingle().Which.Subject.Should().Be(Table);
    }

    [Fact]
    public void Matrix_MapsEveryClassifierCategoryOntoARowWithAKnownVerification()
    {
        // The categories GeoservicesImportService emits for service and resource fidelity classifications.
        string[] resourceCategories = ["identity", "capabilities", "fields", "domains", "subtypes", "relationships", "attachments", "renderers", "time-metadata"];
        string[] serviceCategories = ["identity", "capabilities"];
        string[] verifications =
        [
            MigrationConstructVerifications.SelectionAccounting,
            MigrationConstructVerifications.DataReconciliation,
            MigrationConstructVerifications.CatalogReconciliation,
            MigrationConstructVerifications.AttachmentReconciliation,
            MigrationConstructVerifications.RelationshipApply,
            MigrationConstructVerifications.OperatorReview
        ];
        var rows = MigrationConstructMatrix.Rows.ToDictionary(row => row.Construct, StringComparer.Ordinal);

        MigrationConstructMatrix.Rows.Select(row => row.Construct).Should().OnlyHaveUniqueItems();
        MigrationConstructMatrix.Rows.Should().OnlyContain(row => verifications.Contains(row.Verification));
        foreach (var category in resourceCategories)
        {
            rows.Should().ContainKey(MigrationConstructMatrix.ConstructForCategory(category, serviceScoped: false)!, category);
        }

        foreach (var category in serviceCategories)
        {
            rows.Should().ContainKey(MigrationConstructMatrix.ConstructForCategory(category, serviceScoped: true)!, category);
        }

        // The constructs the acceptance criterion names that no classifier category carries.
        rows.Keys.Should().Contain(["service.type", "service.resource", "resource.geometry", "resource.edit-behavior"]);
    }

    [Fact]
    public void BatchEvaluator_WithoutConstructAccounting_CannotReportFullFidelity()
    {
        var evaluation = MigrationBatchFidelityEvaluator.Evaluate(new MigrationBatchFidelityInput());

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        evaluation.Differences.Should().ContainSingle()
            .Which.Code.Should().Be(MigrationFidelityDifferenceCodes.ConstructAccountingNotExecuted);
    }

    private static MigrationConstructSelection[] FullSelection() =>
    [
        Select(Layer, "field_ops.inspections"),
        Select(Table, "field_ops.inspection_notes")
    ];

    private static MigrationConstructSelection Select(string sourceResourceId, string targetTable)
        => new() { SourceResourceId = sourceResourceId, TargetTable = targetTable };

    private static MigrationManifestArtifact AutomatedManifest() => new()
    {
        SourceKind = "arcgis-geoservices-rest",
        Source = new MigrationSourceIdentity { DisplayName = "Inspections", BaseUrl = ServiceUrl, ServiceType = "FeatureServer" },
        Summary = new MigrationManifestSummary(),
        TargetResources =
        [
            Target(Layer, "layer", "target:layer0", "esriGeometryPoint"),
            Target(Table, "table", "target:table1")
        ],
        FidelityMatrix = new MigrationFidelityMatrix { Cells = AutomatedCells() }
    };

    private static MigrationFidelityMatrixCell[] AutomatedCells() =>
    [
        Cell("attachments", MigrationFidelityAutomationStatuses.Automated, [ImportCompatibilityCodes.Compatible], Layer),
        Cell("capabilities", MigrationFidelityAutomationStatuses.Automated, [ImportCompatibilityCodes.Compatible], Layer, Table, ServiceId),
        Cell("domains", MigrationFidelityAutomationStatuses.Automated, [ImportCompatibilityCodes.Compatible], Layer),
        Cell("fields", MigrationFidelityAutomationStatuses.Automated, [ImportCompatibilityCodes.Compatible], Layer, Table),
        Cell("identity", MigrationFidelityAutomationStatuses.Automated, [ImportCompatibilityCodes.Compatible], Layer, Table, ServiceId),
        Cell("relationships", MigrationFidelityAutomationStatuses.Automated, [ImportCompatibilityCodes.Compatible], Layer)
    ];

    private static MigrationFidelityMatrixCell Cell(string category, string status, string[] codes, params string[] sourceIds) => new()
    {
        Category = category,
        AutomationStatus = status,
        Count = sourceIds.Length,
        SourceIds = sourceIds,
        Codes = codes
    };

    private static MigrationManifestTargetResource Target(
        string sourceResourceId,
        string kind,
        string targetResourceId,
        string? geometryType = null,
        string level = "compatible",
        string code = ImportCompatibilityCodes.Compatible,
        string[]? capabilities = null) => new()
        {
            SourceResourceId = sourceResourceId,
            SourceKind = kind,
            Action = "publish",
            TargetResourceId = targetResourceId,
            TargetServiceName = "inspections",
            TargetResourceName = targetResourceId,
            GeometryType = geometryType,
            Capabilities = capabilities ?? ["Query"],
            Compatibility = Compatibility(level, code)
        };

    private static MigrationCompatibilityAssessment Compatibility(string level, string code)
        => new() { Level = level, Code = code, Reason = "Assessed by the source scan." };

    private static string Serialize(MigrationManifestArtifact manifest)
        => JsonSerializer.Serialize(manifest, MigrationEvidencePackJsonContext.Default.MigrationManifestArtifact);
}
