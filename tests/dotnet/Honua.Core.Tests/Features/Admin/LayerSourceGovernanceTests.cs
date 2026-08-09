// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Tests.Features.Admin;

public sealed class LayerSourceGovernanceTests
{
    [Theory]
    [InlineData("+")]
    [InlineData(":")]
    [InlineData("---")]
    [InlineData("...")]
    [InlineData("MIT++")]
    [InlineData("MIT+Apache-2.0")]
    [InlineData("MIT:Apache-2.0")]
    [InlineData("DocumentRef-:LicenseRef-Custom")]
    [InlineData("DocumentRef-Document:LicenseRef-")]
    [InlineData("DocumentRef-Document::LicenseRef-Custom")]
    [InlineData("MIT OR")]
    [InlineData("(MIT OR Apache-2.0")]
    public void TryCreate_WithMalformedSpdxExpression_RejectsLicense(string license)
    {
        var accepted = LayerSourceGovernance.TryCreate(
            license,
            attribution: null,
            publisher: null,
            licenseUrl: null,
            sourceUrl: null,
            out var governance,
            out var error);

        accepted.Should().BeFalse();
        governance.Should().BeNull();
        error.Should().Be("license must be a syntactically valid SPDX expression or the literal 'proprietary'.");
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData("GPL-2.0+")]
    [InlineData("LicenseRef-Custom")]
    [InlineData("DocumentRef-Document:LicenseRef-Custom")]
    [InlineData("(MIT OR Apache-2.0) AND BSD-3-Clause")]
    [InlineData("GPL-2.0-only WITH Classpath-exception-2.0")]
    public void TryCreate_WithSupportedSpdxExpression_AcceptsLicense(string license)
    {
        var accepted = LayerSourceGovernance.TryCreate(
            license,
            attribution: null,
            publisher: null,
            licenseUrl: null,
            sourceUrl: null,
            out var governance,
            out var error);

        accepted.Should().BeTrue();
        governance.Should().NotBeNull();
        governance!.License.Should().Be(license);
        error.Should().BeNull();
    }
}
