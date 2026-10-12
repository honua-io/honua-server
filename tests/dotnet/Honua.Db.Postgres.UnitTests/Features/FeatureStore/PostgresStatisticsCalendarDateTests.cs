// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class PostgresStatisticsCalendarDateTests
{
    [Theory]
    [InlineData(false, "en-US")]
    [InlineData(false, "en-GB")]
    [InlineData(false, "ja-JP")]
    [InlineData(true, "en-US")]
    [InlineData(true, "en-GB")]
    [InlineData(true, "ja-JP")]
    public void Statistics_CalendarDatesRemainIsoAcrossCultures(bool storageMapped, string culture)
    {
        var provider = storageMapped ? typeof(PostgresStorageMappedFeatureReader) : typeof(FeatureDataAccess);
        var convert = provider.GetMethod("ConvertStatisticsValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            convert.Invoke(null, [new DateOnly(2026, 1, 11)]).Should().Be("2026-01-11");
            convert.Invoke(null, [new DateOnly(2024, 2, 29)]).Should().Be("2024-02-29");
            var timestamp = new DateTimeOffset(2026, 1, 11, 23, 45, 0, TimeSpan.FromHours(14));
            convert.Invoke(null, [timestamp]).Should().Be(timestamp);
            convert.Invoke(null, [1963L]).Should().Be(1963L);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
