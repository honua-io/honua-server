// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Forms;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Forms;

public sealed class FormPackageIdentifierTests
{
    [UnitTheory]
    [InlineData(null)]
    [InlineData("form-0123abcd")]
    [InlineData("Site_Inspection-2")]
    public void IsAcceptable_SafeOrAbsentId_IsAccepted(string? formId)
        => FormPackageIdentifier.IsAcceptable(formId).Should().BeTrue();

    [UnitTheory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("a b")]
    [InlineData("tab\there")]
    [InlineData("caf\u00e9")]
    [InlineData("")]
    [InlineData("IMPORT")]
    public void IsAcceptable_UnsafeOrReservedId_IsRejected(string formId)
        => FormPackageIdentifier.IsAcceptable(formId).Should().BeFalse();

    [UnitTest]
    public void IsAcceptable_BoundsLength()
    {
        FormPackageIdentifier.IsAcceptable(new string('a', FormPackageIdentifier.MaxLength)).Should().BeTrue();
        FormPackageIdentifier.IsAcceptable(new string('a', FormPackageIdentifier.MaxLength + 1)).Should().BeFalse();
    }
}
