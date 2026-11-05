// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Validation;

public sealed class BoundingBoxOrdinatesTests
{
    [UnitTheory]
    [InlineData("1,2,3,4", new[] { 1d, 2, 3, 4 })]
    [InlineData(" -1.5 , 2e1 ,3,4 ", new[] { -1.5d, 20, 3, 4 })]
    [InlineData("1,2,0,3,4,10", new[] { 1d, 2, 0, 3, 4, 10 })]
    public void Parse_FourOrSixFiniteNumbers_ReturnsThem(string text, double[] expected)
    {
        var parsed = BoundingBoxOrdinates.Parse(text);
        parsed.Succeeded.Should().BeTrue();
        parsed.Ordinates.Should().Equal(expected);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("1,2,3,4,5,6,7")]
    [InlineData("1,,3,4")]
    [InlineData("1,2,x,4")]
    [InlineData("1;2;3;4")]
    [InlineData("NaN,2,3,4")]
    [InlineData("1,2,Infinity,4")]
    [InlineData("-Infinity,2,3,4")]
    [InlineData("1,2,3,1e400")]
    public void Parse_AnythingElse_IsRejected(string? text)
        => BoundingBoxOrdinates.Parse(text).Succeeded.Should().BeFalse();

    [UnitTest]
    public void Horizontal_SixOrdinates_SkipsTheZValues()
        => BoundingBoxOrdinates.Horizontal([1, 2, 0, 3, 4, 10]).Should().Be((1d, 2d, 3d, 4d));

    [UnitTest]
    public void Horizontal_FourOrdinates_ReturnsThemInOrder()
        => BoundingBoxOrdinates.Horizontal([1, 2, 3, 4]).Should().Be((1d, 2d, 3d, 4d));

    [UnitTest]
    public void Horizontal_AnyOtherCount_IsRejected()
    {
        var read = () => BoundingBoxOrdinates.Horizontal([1, 2, 3]);
        read.Should().Throw<ArgumentOutOfRangeException>();
    }
}
