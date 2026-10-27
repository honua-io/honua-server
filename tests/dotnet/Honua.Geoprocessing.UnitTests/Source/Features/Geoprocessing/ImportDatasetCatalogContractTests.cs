// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// The 2026.1 import contract keeps source attributes in one JSON <c>properties</c> column;
/// the catalog must not advertise a typed-layer flattening the pipeline does not perform
/// (typed-layer flattening is honua-server#6640).
/// </summary>
public sealed class ImportDatasetCatalogContractTests
{
    [UnitTest]
    public void ImportDataset_Description_StatesTheJsonPropertiesContract()
    {
        var process = new BuiltInProcessCatalog().GetProcess("import.dataset");

        process.Should().NotBeNull();
        process!.Description.Should().NotContain("typed layer");
        process.Description.Should().Contain("single JSON properties column");
        process.Parameters.Should().NotContain(
            parameter => parameter.Description.Contains("typed layer", StringComparison.Ordinal));
    }
}
