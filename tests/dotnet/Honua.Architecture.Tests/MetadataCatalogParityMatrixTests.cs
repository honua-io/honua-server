// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Keeps the SDK metadata/catalog parity matrix usable as a contract instead of
/// a prose-only planning note.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class MetadataCatalogParityMatrixTests
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    {
        "public-catalog-read",
        "admin-control-plane-metadata-read",
        "external-source-inventory-read",
        "protocol-native-metadata-read"
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "implemented",
        "planned"
    };

    private static readonly string[] RequiredEntryIds =
    [
        "ogc-records-catalog",
        "geoservices-service-directory",
        "admin-service-catalog",
        "admin-connection-layer-inventory",
        "admin-metadata-resources",
        "admin-metadata-manifests",
        "admin-style-metadata",
        "admin-scene-dataset-registry",
        "geoservices-featureserver-metadata",
        "geoservices-mapserver-metadata",
        "geoservices-imageserver-metadata",
        "ogc-api-features-metadata",
        "ogc-api-maps-metadata",
        "ogc-api-tiles-metadata",
        "ogc-api-coverages-metadata",
        "stac-catalog",
        "wfs-capabilities-and-schema",
        "wms-wmts-wcs-capabilities",
        "odata-service-metadata",
        "scene-sdk-metadata",
        "migration-source-inventory",
        "legacy-external-discovery"
    ];

    [ArchitectureTest]
    public void MetadataCatalogEndpointInventory_ShouldDeclareSdkParityContractFields()
    {
        using var document = LoadInventory();
        var root = document.RootElement;

        root.GetProperty("schemaVersion").GetString().Should().Be("1.0.0");
        root.GetProperty("canonicalIssue").GetString().Should().Be("honua-io/honua-server#955");
        root.GetProperty("parentEpic").GetString().Should().Be("honua-io/honua-server#954");
        root.GetProperty("relatedIssues")
            .EnumerateArray()
            .Select(static issue => issue.GetString())
            .Should()
            .Contain("honua-io/honua-server#952");

        var entries = root.GetProperty("entries").EnumerateArray().ToArray();
        entries.Should().NotBeEmpty();

        var ids = entries.Select(static entry => RequiredString(entry, "id")).ToArray();
        ids.Should().OnlyHaveUniqueItems("matrix entry ids are SDK implementation keys");
        ids.Should().Contain(RequiredEntryIds);

        foreach (var entry in entries)
        {
            var id = RequiredString(entry, "id");
            RequiredString(entry, "title").Should().NotBeNullOrWhiteSpace(id);
            AllowedStatuses.Should().Contain(RequiredString(entry, "status"), id);
            AllowedCategories.Should().Contain(RequiredString(entry, "category"), id);
            RequiredString(entry, "audience").Should().NotBeNullOrWhiteSpace(id);
            RequiredString(entry, "authPosture").Should().NotBeNullOrWhiteSpace(id);
            RequiredString(entry, "responseKind").Should().NotBeNullOrWhiteSpace(id);
            RequiredString(entry, "paginationFiltering").Should().NotBeNullOrWhiteSpace(id);
            RequiredString(entry, "testExpectation").Should().NotBeNullOrWhiteSpace(id);

            var endpointPatterns = entry.GetProperty("endpointPatterns")
                .EnumerateArray()
                .Select(static pattern => pattern.GetString())
                .ToArray();
            endpointPatterns.Should().NotBeEmpty($"{id} must list endpoint path patterns");
            endpointPatterns.Should().OnlyContain(
                pattern => !string.IsNullOrWhiteSpace(pattern),
                $"{id} must list endpoint path patterns");

            var sdkTargets = entry.GetProperty("sdkTargets");
            RequiredString(sdkTargets, "dotnet").Should().NotBeNullOrWhiteSpace(id);
            RequiredString(sdkTargets, "javascript").Should().NotBeNullOrWhiteSpace(id);
            RequiredString(sdkTargets, "python").Should().NotBeNullOrWhiteSpace(id);
        }
    }

    [ArchitectureTest]
    public void MetadataCatalogMarkdown_ShouldLinkEveryMachineReadableEntry()
    {
        using var document = LoadInventory();
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var markdownPath = Path.Combine(repoRoot, "docs", "developer", "metadata-catalog-parity-matrix.md");
        var markdown = File.ReadAllText(markdownPath);

        markdown.Should().Contain("honua-server#955");
        markdown.Should().Contain("honua-server#954");
        markdown.Should().Contain("honua-server#952");
        markdown.Should().Contain("metadata-catalog-endpoints.v1.json");

        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var id = RequiredString(entry, "id");
            markdown.Should().Contain($"`{id}`", $"the markdown matrix must list inventory entry {id}");
        }
    }

    private static JsonDocument LoadInventory()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = Path.Combine(repoRoot, "docs", "developer", "metadata-catalog-endpoints.v1.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        element.TryGetProperty(propertyName, out var value)
            .Should()
            .BeTrue($"property {propertyName} is required");
        value.ValueKind.Should().Be(JsonValueKind.String, $"property {propertyName} must be a string");
        return value.GetString() ?? string.Empty;
    }
}
