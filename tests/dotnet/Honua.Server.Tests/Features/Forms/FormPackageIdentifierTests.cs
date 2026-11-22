// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Forms;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Forms;

public sealed class FormPackageIdentifierTests
{
    [UnitTheory]
    [InlineData(null, true)]
    [InlineData("form-0123abcd", true)]
    [InlineData("Site_Inspection-2", true)]
    [InlineData("../evil", false)]
    [InlineData("a/b", false)]
    [InlineData("a.b", false)]
    [InlineData("a b", false)]
    [InlineData("tab\there", false)]
    [InlineData("café", false)]
    [InlineData("", false)]
    [InlineData("IMPORT", false)]
    public void IsAcceptable_AllowsOnlySafeRouteSegments(string? formId, bool expected)
        => FormPackageIdentifier.IsAcceptable(formId).Should().Be(expected);

    [UnitTest]
    public void IsAcceptable_BoundsLength()
    {
        FormPackageIdentifier.IsAcceptable(new string('a', FormPackageIdentifier.MaxLength)).Should().BeTrue();
        FormPackageIdentifier.IsAcceptable(new string('a', FormPackageIdentifier.MaxLength + 1)).Should().BeFalse();
    }
}
