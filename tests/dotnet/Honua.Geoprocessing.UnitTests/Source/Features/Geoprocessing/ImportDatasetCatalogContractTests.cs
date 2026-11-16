// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

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
