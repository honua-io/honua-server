// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Geoprocessing;

public sealed class FeatureLayerCopyDateTimeTests
{
    [UnitTest]
    public void ZoneLessUtcDateTime_OffsetText_BecomesUtcWallClock()
    {
        PostgresFeatureLayerCopyService.ZoneLessUtcDateTime("2024-01-01T14:00:00+02:00")
            .Should().Be("2024-01-01T12:00:00.0000000");
    }

    [UnitTest]
    public void ZoneLessUtcDateTime_UnzonedText_StaysCivilTime()
    {
        PostgresFeatureLayerCopyService.ZoneLessUtcDateTime("2024-01-01T14:00:00")
            .Should().Be("2024-01-01T14:00:00");
    }
}
