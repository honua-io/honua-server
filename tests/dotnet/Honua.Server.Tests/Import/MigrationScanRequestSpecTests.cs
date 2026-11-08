// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.Migration;

namespace Honua.Server.Tests.Import;

public sealed class MigrationScanRequestSpecTests
{
    [UnitTheory]
    [InlineData("ImportGeoservicesStartRequest")]
    [InlineData("ImportMigrationBatchLayerSpec")]
    [InlineData("MigrationInventoryScanRequest")]
    public void TargetInputsSpec_DescribesPresenceAwareFolderAndReusablePlan(string schemaName)
    {
        using var spec = JsonDocument.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));
        var schemas = spec.RootElement.GetProperty("components").GetProperty("schemas");
        var properties = schemas.GetProperty(schemaName).GetProperty("properties");
        properties.GetProperty("folderId").GetProperty("nullable").GetBoolean().Should().BeTrue();
        properties.GetProperty("folderId").GetProperty("description").GetString().Should().Contain("explicit null");
        properties.GetProperty("publicName").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("namespace").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("plan").GetProperty("$ref").GetString().Should().Be("#/components/schemas/ImportGeoservicesPlan");
        var plan = schemas.GetProperty("ImportGeoservicesPlan").GetProperty("properties");
        plan.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            GeoservicesImportApiJsonContext.Default.GeoservicesImportPlan.Properties.Select(property => property.Name));
        plan.TryGetProperty("tenantId", out _).Should().BeFalse();
        plan.TryGetProperty("tenantSource", out _).Should().BeFalse();
        schemas.GetProperty("ImportGeoservicesJobQueuedResponse").GetProperty("properties")
            .GetProperty("plan").GetProperty("$ref").GetString().Should().Be("#/components/schemas/ImportGeoservicesPlan");
    }

    [UnitTest]
    public void SourceKindEnum_ListsCurrentAndLegacyGeoServicesRestValuesOnce()
    {
        using var spec = JsonDocument.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));
        var sourceKinds = spec.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("MigrationInventoryScanRequest").GetProperty("properties")
            .GetProperty("sourceKind").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        sourceKinds.Should().OnlyHaveUniqueItems();
        sourceKinds.Should().Contain([MigrationSourceKinds.GeoServicesRest, MigrationSourceKinds.LegacyGeoServicesRest]);
    }
}
