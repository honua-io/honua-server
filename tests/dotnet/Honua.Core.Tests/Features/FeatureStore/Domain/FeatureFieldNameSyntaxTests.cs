// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

public sealed class FeatureFieldNameSyntaxTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("field_2")]
    [InlineData("eo:cloud_cover")]
    [InlineData("owner.name")]
    [InlineData("cloud-cover")]
    public void IsValid_WithSupportedFieldName_ReturnsTrue(string fieldName)
    {
        FeatureFieldNameSyntax.IsValid(fieldName).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(":field")]
    [InlineData(".field")]
    [InlineData("-field")]
    [InlineData("owner name")]
    [InlineData("name/name")]
    [InlineData("name;DROP")]
    [InlineData("\"name\"")]
    public void IsValid_WithUnsupportedFieldName_ReturnsFalse(string? fieldName)
    {
        FeatureFieldNameSyntax.IsValid(fieldName).Should().BeFalse();
    }
}
