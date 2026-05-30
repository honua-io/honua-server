// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Asserts the reconciled GeoETL connector/transform processes are present in the
/// built-in process catalog and therefore surface automatically as workflow nodes via
/// ProcessCatalogWorkflowNodeProvider (node type id <c>process:&lt;processId&gt;</c>),
/// which is the #1185 add-a-capability contract.
/// </summary>
public sealed class GeoEtlCatalogReconciliationTests
{
    private static readonly string[] ReconciledProcessIds =
    {
        // Transforms
        "transform.attribute-rename",
        "transform.attribute-cast",
        "transform.computed-field",
        "transform.attribute-filter",
        "transform.spatial-filter",
        "transform.clip",
        "transform.dedup",
        "transform.reproject",
        // Sources
        "source.geojson",
        "source.csv",
        // Sinks
        "sink.geojson-file",
        "sink.quarantine",
        "sink.external-postgis",
    };

    private static readonly string[] ExpectedTransformIds = { "transform.reproject", "transform.clip", "transform.dedup" };
    private static readonly string[] ExpectedSourceIds = { "source.geojson", "source.csv" };
    private static readonly string[] ExpectedSinkIds = { "sink.geojson-file", "sink.quarantine", "sink.external-postgis" };

    [UnitTest]
    public void Catalog_ContainsEveryReconciledProcess()
    {
        var catalog = new BuiltInProcessCatalog();

        foreach (var processId in ReconciledProcessIds)
        {
            var process = catalog.GetProcess(processId);
            process.Should().NotBeNull($"process '{processId}' must be in the catalog");
            process!.Parameters.Should().NotBeEmpty();
            process.OutputArtifactKinds.Should().NotBeEmpty();
        }
    }

    [UnitTest]
    public void Catalog_ExposesTransformSourceSinkCategories()
    {
        var catalog = new BuiltInProcessCatalog();

        catalog.GetProcessesByCategory("transform").Select(p => p.ProcessId)
            .Should().Contain(ExpectedTransformIds);
        catalog.GetProcessesByCategory("source").Select(p => p.ProcessId)
            .Should().Contain(ExpectedSourceIds);
        catalog.GetProcessesByCategory("sink").Select(p => p.ProcessId)
            .Should().Contain(ExpectedSinkIds);
    }

    [UnitTest]
    public async Task NodeProvider_SurfacesEveryReconciledProcessAsNode()
    {
        IProcessCatalog catalog = new BuiltInProcessCatalog();
        var provider = new ProcessCatalogWorkflowNodeProvider(catalog);

        var nodes = await provider.ListNodesAsync();
        var nodeTypeIds = nodes.Select(n => n.NodeTypeId).ToHashSet();

        foreach (var processId in ReconciledProcessIds)
        {
            nodeTypeIds.Should().Contain(
                ProcessCatalogWorkflowNodeProvider.ToNodeTypeId(processId),
                $"process '{processId}' must surface as a workflow node");
        }
    }
}
