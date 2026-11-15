// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Tests.Features.Import;

public class EsriProjectedCrsNamesTests
{
    [Fact]
    public void TryGet_WithAmbiguousProjectedName_ReturnsFalse()
    {
        // The name is an ESRI alias of two deprecated projected codes, so it is not a unique match.
        EsriProjectedCrsNames.TryGet("NAD_1983_CSRS_MTM_2_SCoPQ", out _).Should().BeFalse();
    }
}
