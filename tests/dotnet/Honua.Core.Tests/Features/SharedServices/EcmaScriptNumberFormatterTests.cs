// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Services;

namespace Honua.Core.Tests.Features.SharedServices;

public sealed class EcmaScriptNumberFormatterTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(30d, "30")]
    [InlineData(-0.5d, "-0.5")]
    [InlineData(0.000001d, "0.000001")]
    [InlineData(1e-7d, "1e-7")]
    [InlineData(1e20d, "100000000000000000000")]
    [InlineData(1e21d, "1e+21")]
    [InlineData(123.456d, "123.456")]
    [InlineData(-0d, "0")]
    public void Format_MatchesEcmaScriptNumberToString(double value, string expected)
        => Assert.Equal(expected, EcmaScriptNumberFormatter.Format(value));
}
