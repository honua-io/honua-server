// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Db.Postgres.Security.Tests;

public sealed class OperationalSchemaPolicyTests
{
    [UnitTheory]
    [InlineData("public", true)]
    [InlineData("PUBLIC", true)]
    [InlineData("public,analytics", true)]
    [InlineData("\"MixedCase\", public", true)]
    [InlineData("MixedCase", false)]
    [InlineData("\"PUBLIC\"", false)]
    [InlineData("public,honua", false)]
    [InlineData("private_metadata", false)]
    [InlineData("pg_catalog", false)]
    [InlineData("pg_temp", false)]
    [InlineData("information_schema", false)]
    [InlineData("unlisted", false)]
    [InlineData("\"$user\",public", false)]
    [InlineData("public,", false)]
    [InlineData("", false)]
    [InlineData("\"public\",\"analytics\"", true)]
    public void SearchPath_UsesOnlyConfiguredLiteralSchemas(string searchPath, bool allowed)
    {
        var policy = new PostgresSchemaConfiguration("private_metadata", "analytics",
            ["public", "analytics", "MixedCase", "honua", "pg_catalog", "private_metadata"]);
        Assert.Equal(allowed, policy.IsAllowedSearchPath(searchPath));
    }
}
