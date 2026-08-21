using System.Text.Json;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed class ProtocolHarnessAssignmentDriftTests
{
    private const string ContractPath = "docs/gis/data/protocol-harness-assignments.v1.json";

    [Fact]
    public void GovernedProtocolHarnessOperations_MapToExistingExecutableTests()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var contractPath = Path.Combine(repositoryRoot, ContractPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(contractPath));
        var root = document.RootElement;

        Assert.Equal("honua.server-protocol-harness-assignments/v1", root.GetProperty("schema").GetString());
        Assert.Equal("https://github.com/honua-io/honua-server/issues/3388", root.GetProperty("tracking_issue").GetString());

        var assignments = root.GetProperty("assignments").EnumerateArray().ToArray();
        Assert.Equal(32, assignments.Length);
        Assert.Equal(20, assignments.Select(row => row.GetProperty("capability_key").GetString()).Distinct().Count());

        using var featureCatalog = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "gis", "data", "feature-catalog.json")));
        var featureEntries = featureCatalog.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        var operationKeys = assignments
            .Select(row => string.Join('|',
                row.GetProperty("capability_key").GetString(),
                row.GetProperty("surface").GetString(),
                row.GetProperty("operation").GetString()))
            .ToArray();
        Assert.Equal(operationKeys.Length, operationKeys.Distinct(StringComparer.Ordinal).Count());

        var executableTests = ArchitectureTestHelpers.IntegrationTestMethods().ToArray();

        foreach (var assignment in assignments)
        {
            var capabilityKey = assignment.GetProperty("capability_key").GetString()!;
            var catalogCapabilityKey = assignment.TryGetProperty("catalog_capability_key", out var catalogCapability)
                ? catalogCapability.GetString()!
                : capabilityKey;
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
