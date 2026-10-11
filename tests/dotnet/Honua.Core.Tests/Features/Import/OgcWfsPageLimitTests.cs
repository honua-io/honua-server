// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Tests.Features.Import;

public sealed class OgcWfsPageLimitTests
{
    [Fact]
    public void ClampPageSize_CapsAtTenThousand()
    {
        OgcWfsImportRequest.ClampPageSize(50_000_000).Should().Be(OgcWfsImportRequest.MaxPageSize);
        OgcWfsImportRequest.ClampPageSize(25).Should().Be(25);
    }

    [Fact]
    public void EnsurePageWithinRequest_RejectsAPageLargerThanRequested()
    {
        var act = () => OgcWfsImportRequest.EnsurePageWithinRequest(11, 10);
        act.Should().Throw<FormatException>().WithMessage("*11*10*");
    }
}
