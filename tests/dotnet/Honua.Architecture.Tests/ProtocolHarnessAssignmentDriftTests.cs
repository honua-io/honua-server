using System.Text.Json;
using System.Text.RegularExpressions;
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
        Assert.NotEmpty(assignments);
        Assert.Equal(20, assignments.Select(row => row.GetProperty("capability_key").GetString()).Distinct().Count());

        using var featureCatalog = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "gis", "data", "feature-catalog.json")));
        var governedSurfaces = featureCatalog.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.TryGetProperty("capability", out var capability)
                && capability.ValueKind == JsonValueKind.String
                && entry.TryGetProperty("proof_ledger_surface", out var surface)
                && surface.ValueKind == JsonValueKind.String)
            .GroupBy(entry => entry.GetProperty("capability").GetString()!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.GetProperty("proof_ledger_surface").GetString()!)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var operationKeys = assignments
            .Select(row => string.Join('|',
                row.GetProperty("capability_key").GetString(),
                row.GetProperty("surface").GetString(),
                row.GetProperty("operation").GetString()))
            .ToArray();
        Assert.Equal(operationKeys.Length, operationKeys.Distinct(StringComparer.Ordinal).Count());

        var sourceFiles = Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "tests"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(path => new SourceFile(File.ReadAllText(path)))
            .ToArray();

        foreach (var assignment in assignments)
        {
            var capabilityKey = assignment.GetProperty("capability_key").GetString()!;
            var surface = assignment.GetProperty("surface").GetString()!;
            Assert.True(
                governedSurfaces.TryGetValue(capabilityKey, out var surfaces)
                && surfaces.Contains(surface),
                $"{capabilityKey} uses surface {surface}, which is absent from feature-catalog.json.");

            var operation = assignment.GetProperty("operation").GetString()!;
            var normalizedOperation = operation.Split('?', 2)[0];
            var testIds = assignment.GetProperty("test_ids").EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            Assert.NotEmpty(testIds);
            Assert.Equal(testIds.Length, testIds.Distinct(StringComparer.Ordinal).Count());

            foreach (var testId in testIds)
            {
                var separator = testId.LastIndexOf('.');
                Assert.True(separator > 0 && separator < testId.Length - 1, $"Invalid test ID: {testId}");
                var className = testId[..separator];
                var methodName = testId[(separator + 1)..];
                var classPattern = new Regex($@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.CultureInvariant);
                var methodPattern = new Regex($@"\b{Regex.Escape(methodName)}\s*\(", RegexOptions.CultureInvariant);

                var source = Assert.Single(
                    sourceFiles,
                    source => classPattern.IsMatch(source.Content) && methodPattern.IsMatch(source.Content));
                var methodContractPattern = new Regex(
                    $@"(?<attributes>(?:\s*\[[^\]]+\])*)\s*public\s+(?:async\s+)?Task(?:<[^>]+>)?\s+{Regex.Escape(methodName)}\s*\(",
                    RegexOptions.CultureInvariant);
                var methodContract = methodContractPattern.Match(source.Content);
                Assert.True(methodContract.Success, $"Cannot resolve endpoint metadata for {testId}.");
                var endpoints = Regex.Matches(
                        methodContract.Groups["attributes"].Value,
                        "\\[Endpoint\\(\\\"(?<endpoint>[^\\\"]+)\\\"\\)\\]",
                        RegexOptions.CultureInvariant)
                    .Select(match => match.Groups["endpoint"].Value)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains(normalizedOperation, endpoints);
            }
        }
    }

    private sealed record SourceFile(string Content);
}
