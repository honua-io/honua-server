// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Tests.Features.Admin;

public sealed class LayerSourceGovernanceTests
{
    [Fact]
    public void ToMetadataLinks_WithStandaloneSpdxLicense_DerivesCanonicalLicenseUrl()
    {
        var accepted = LayerSourceGovernance.TryCreate(
            "MIT",
            attribution: null,
            publisher: null,
            licenseUrl: null,
            sourceUrl: null,
            out var governance,
            out var error);

        var link = governance!.ToMetadataLinks().Should().ContainSingle().Which;

        accepted.Should().BeTrue();
        error.Should().BeNull();
        link.Href.Should().Be("https://spdx.org/licenses/MIT.html");
        link.Rel.Should().Be("license");
        link.Title.Should().Be("MIT");
        link.ManagedBy.Should().Be(LayerSourceGovernance.LinkManager);
    }

    [Fact]
    public void ToMetadataLinks_WithSpdxExpressionWithoutUrl_DoesNotInventLicenseUrl()
    {
        var accepted = LayerSourceGovernance.TryCreate(
            "MIT OR Apache-2.0",
            attribution: null,
            publisher: null,
            licenseUrl: null,
            sourceUrl: null,
            out var governance,
            out var error);

        accepted.Should().BeTrue();
        error.Should().BeNull();
        governance!.ToMetadataLinks().Should().BeEmpty();
    }

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
    [InlineData("LicenseRef-Custom+")]
    [InlineData("AdditionRef-Custom")]
    [InlineData("DocumentRef-Document:AdditionRef-Custom")]
    [InlineData("MIT WITH LicenseRef-Custom")]
    [InlineData("MIT WITH GPL-2.0+")]
    [InlineData("MIT WITH DocumentRef-Document:LicenseRef-Custom")]
    [InlineData("(MIT OR Apache-2.0) WITH LLVM-exception")]
    [InlineData("(MIT) WITH LLVM-exception")]
    [InlineData("MIT OR(Apache-2.0)")]
    [InlineData("MIT AND(Apache-2.0)")]
    [InlineData("MIT OR")]
    [InlineData("(MIT OR Apache-2.0")]
    [InlineData("proprietary+")]
    [InlineData("MIT OR proprietary")]
    [InlineData("Not-A-License")]
    [InlineData("MIT WITH Not-An-Exception")]
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
    [InlineData("MIT WITH AdditionRef-Custom")]
    [InlineData("MIT WITH DocumentRef-Document:AdditionRef-Custom")]
    [InlineData("MIT OR (Apache-2.0 AND BSD-3-Clause)")]
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
