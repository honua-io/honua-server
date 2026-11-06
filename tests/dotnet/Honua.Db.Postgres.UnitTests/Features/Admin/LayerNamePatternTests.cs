// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class LayerNamePatternTests
{
    [UnitTest]
    public void BlankQuery_MatchesEveryName()
    {
        LayerNamePattern.ForIlike(null).Should().BeNull();
        LayerNamePattern.ForIlike("   ").Should().BeNull();
    }

    [UnitTest]
    public void Wildcards_AreEscapedBeforeTheContainsWrap()
    {
        LayerNamePattern.ForIlike(" 100%_a\\b ").Should().Be("%100\\%\\_a\\\\b%");
    }
}
