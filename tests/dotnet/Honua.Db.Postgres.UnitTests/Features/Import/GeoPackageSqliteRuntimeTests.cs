// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Data.Sqlite;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoPackageSqliteRuntimeTests
{
    [Fact]
    public void Open_LoadedNativeEngine_IncludesAggregateBoundsFix()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var actual = Version.Parse(connection.ServerVersion);
        var minimum = new Version(3, 50, 2);

        Assert.True(actual >= minimum,
            $"GeoPackage processing loads native SQLite {actual}; aggregate bounds safety requires {minimum} or later.");
    }
}
