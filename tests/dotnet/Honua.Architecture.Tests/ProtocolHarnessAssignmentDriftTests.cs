using System.Text.Json;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed class ProtocolHarnessAssignmentDriftTests
{
    private static readonly HashSet<string> ExpectedOperations = new(StringComparer.Ordinal)
    {
        "ai.grounding|spec-grounding|POST /v1/grounding/spec/mutate",
        "alerts.evaluation|control-plane-admin|POST /api/v1/admin/alerts/rules/test",
        "analytics.buffer-aggregate|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate",
        "analytics.clustering|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters",
        "analytics.content|analysis-content|POST /api/v1/analysis/content/items",
        "analytics.content|analysis-content|GET /api/v1/analysis/content/items/{itemId}",
        "analytics.content|analysis-content|GET /api/v1/analysis/content/items/{itemId}/versions/latest",
        "analytics.content|analysis-content|GET /api/v1/analysis/content/items/{itemId}/versions/{contentVersion}",
        "analytics.content|analysis-content|POST /api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/preview",
        "analytics.content|analysis-content|GET /api/v1/analysis/artifacts/{artifactId}",
        "analytics.density|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity",
        "analytics.line-of-sight|elevation-query-profile|POST /elevation/{datasetId}/line-of-sight",
        "analytics.slice|elevation-query-profile|POST /elevation/{datasetId}/slice",
        "analytics.spatial-join|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin",
        "analytics.sun-shadow|elevation-query-profile|POST /elevation/{datasetId}/sun-shadow",
        "analytics.viewshed|elevation-query-profile|POST /elevation/{datasetId}/viewshed",
        "editing.featureserver-edits|feature-server|POST /rest/services/{serviceId}/FeatureServer/applyEdits",
        "editing.featureserver-edits|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits",
        "editing.featureserver-edits|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures",
        "editing.featureserver-edits|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures",
        "editing.featureserver-edits|feature-server|POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures",
        "import.file|control-plane-admin|POST /api/v1/admin/import/upload",
        "printing.pdf-output|printing-tools|POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute",
        "serve.geoservices-vectortileserver|vector-tile-server|GET /rest/services/{serviceId}/VectorTileServer",
        "serve.geoservices-vectortileserver|vector-tile-server|GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf",
        "serve.ogc-api-features|ogc-api-features|POST /ogc/features/collections/{collectionId}/items",
        "serve.ogc-api-features|ogc-api-features|PUT /ogc/features/collections/{collectionId}/items/{featureId}",
        "serve.ogc-api-features|ogc-api-features|PATCH /ogc/features/collections/{collectionId}/items/{featureId}",
        "serve.ogc-api-features|ogc-api-features|DELETE /ogc/features/collections/{collectionId}/items/{featureId}",
        "serve.ogc-api-edr|ogc-api-edr|GET /edr",
        "serve.ogc-api-edr|ogc-api-edr|GET /edr/conformance",
        "serve.ogc-api-edr|ogc-api-edr|GET /edr/collections",
        "serve.ogc-api-edr|ogc-api-edr|GET /edr/collections/{collectionId}",
        "serve.ogc-api-edr|ogc-api-edr|GET /edr/collections/{collectionId}/position",
        "serve.ogc-api-edr|ogc-api-edr|GET /edr/collections/{collectionId}/cube",
        "serve.sensorthings|sensorthings-1.1|GET /sta/v1.1/Things",
        "serve.sensorthings|sensorthings-1.1|POST /sta/v1.1/Observations",
        "serve.wfs|wfs-2.0|POST /wfs",
        "styling.auto-suggest|control-plane-admin|POST /api/v1/admin/metadata/layers/{layerId}/suggest-style",
        "temporal.extent-discovery|feature-server|GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent",
        "temporal.filtering|feature-server|GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?time={time}",
        "temporal.histogram|feature-server|GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins",
    };
    private static readonly Dictionary<string, string> AllowedCatalogCapabilityCrosswalks =
        new(StringComparer.Ordinal)
        {
            ["styling.auto-suggest"] = "admin.control-plane",
            ["temporal.filtering"] = "serve.geoservices-featureserver",
        };

    [Fact]
    public void GovernedProtocolHarnessOperations_MapToExistingExecutableTests()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var contractPath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot, "docs", "gis", "data", "protocol-harness-assignments.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(contractPath));
        var root = document.RootElement;

        Assert.Equal("honua.server-protocol-harness-assignments/v1", root.GetProperty("schema").GetString());
        Assert.Equal("https://github.com/honua-io/honua-server/issues/3388", root.GetProperty("tracking_issue").GetString());

        var assignments = root.GetProperty("assignments").EnumerateArray().ToArray();
        Assert.Equal(42, assignments.Length);
        Assert.Equal(23, assignments.Select(row => row.GetProperty("capability_key").GetString()).Distinct().Count());

        using var featureCatalog = JsonDocument.Parse(File.ReadAllText(
            ArchitectureTestHelpers.CombinePath(repositoryRoot, "docs", "gis", "data", "feature-catalog.json")));
        var featureEntries = featureCatalog.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        var operationKeys = assignments
            .Select(row => string.Join('|',
                row.GetProperty("capability_key").GetString(),
                row.GetProperty("surface").GetString(),
                row.GetProperty("operation").GetString()))
            .ToArray();
        Assert.Equal(operationKeys.Length, operationKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.True(ExpectedOperations.SetEquals(operationKeys), "The governed protocol-harness operation identities changed.");

        var executableTests = ArchitectureTestHelpers.IntegrationTestMethods().ToArray();

        foreach (var assignment in assignments)
        {
            var capabilityKey = assignment.GetProperty("capability_key").GetString()!;
            var hasCatalogCrosswalk = assignment.TryGetProperty("catalog_capability_key", out var catalogCapability);
            var catalogCapabilityKey = hasCatalogCrosswalk ? catalogCapability.GetString()! : capabilityKey;
            if (AllowedCatalogCapabilityCrosswalks.TryGetValue(capabilityKey, out var expectedCatalogCapability))
            {
                Assert.True(hasCatalogCrosswalk, $"{capabilityKey} must declare its reviewed catalog crosswalk.");
                Assert.Equal(expectedCatalogCapability, catalogCapabilityKey);
            }
            else
            {
                Assert.False(hasCatalogCrosswalk, $"{capabilityKey} cannot override its catalog capability.");
            }
            var surface = assignment.GetProperty("surface").GetString()!;
            var operation = assignment.GetProperty("operation").GetString()!;
            var separatorIndex = operation.IndexOf(' ');
            Assert.True(separatorIndex > 0, $"Invalid method/route operation: {operation}");
            var httpMethod = operation[..separatorIndex];
            var route = operation[(separatorIndex + 1)..].Split('?', 2)[0];
            var testIds = assignment.GetProperty("test_ids").EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            Assert.NotEmpty(testIds);
            Assert.Equal(testIds.Length, testIds.Distinct(StringComparer.Ordinal).Count());

            var catalogEntry = Assert.Single(
                featureEntries,
                entry => entry.GetProperty("capability").GetString() == catalogCapabilityKey
                    && entry.GetProperty("proof_ledger_surface").GetString() == surface
                    && entry.GetProperty("method").GetString() == httpMethod
                    && entry.GetProperty("route").GetString() == route);
            var provingTests = catalogEntry.GetProperty("proving_tests").EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => value is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            foreach (var testId in testIds)
            {
                var separator = testId.LastIndexOf('.');
                Assert.True(separator > 0 && separator < testId.Length - 1, $"Invalid test ID: {testId}");
                var className = testId[..separator];
                var methodName = testId[(separator + 1)..];
                var testMethod = Assert.Single(
                    executableTests,
                    method => method.DeclaringType?.Name == className && method.Name == methodName);
                var fact = Assert.Single(testMethod.GetCustomAttributes(inherit: true).OfType<FactAttribute>());
                Assert.True(string.IsNullOrEmpty(fact.Skip), $"Governed test is skipped: {testId}");
                Assert.Contains(provingTests, fullyQualified => fullyQualified.EndsWith('.' + testId, StringComparison.Ordinal));
            }
        }
    }
}
