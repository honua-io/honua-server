// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class ImportPhysicalTableNameTests
{
    [UnitTest]
    public void Canonical_PreservesPublishedNamesAndBoundsLongIdentifiers()
    {
        Assert.Equal("imported_roads", ImportPhysicalTableName.Canonical("Roads"));
        var first = ImportPhysicalTableName.Canonical(new string('a', 63));
        var second = ImportPhysicalTableName.Canonical(new string('a', 62) + "b");
        Assert.Equal(40, first.Length);
        Assert.NotEqual(first, second);
        Assert.Equal("imported_" + new string('a', 63), ImportPhysicalTableName.Legacy(new string('a', 63)));
    }
}
