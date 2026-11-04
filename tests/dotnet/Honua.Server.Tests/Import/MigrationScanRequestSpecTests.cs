// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class MigrationScanRequestSpecTests
{
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
