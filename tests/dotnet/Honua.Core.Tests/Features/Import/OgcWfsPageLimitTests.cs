// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Import;

public sealed class OgcWfsPageLimitTests
{
    [UnitTest]
    public void ClampPageSize_CapsAtTenThousand()
    {
        OgcWfsImportRequest.ClampPageSize(50_000_000).Should().Be(OgcWfsImportRequest.MaxPageSize);
        OgcWfsImportRequest.ClampPageSize(25).Should().Be(25);
    }

    [UnitTest]
    public void EnsurePageWithinRequest_RejectsAPageLargerThanRequested()
    {
        var act = () => OgcWfsImportRequest.EnsurePageWithinRequest(11, 10);
        act.Should().Throw<FormatException>().WithMessage("*11*10*");
    }
}
