// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// A schema becomes acceptable on a connection's search path only by being configured under
    /// <c>Database:OperationalSchemas</c>, the remedy #5391 gives operators and the one isolated
    /// test hosts use for their fixture schema. Configuring a schema never admits the metadata
    /// schema beside it.
    /// </summary>
    [UnitTheory]
    [InlineData("test_fixture_0123,public", true)]
    [InlineData("test_fixture_0123", true)]
    [InlineData("test_fixture_0123,honua,public", false)]
    [InlineData("test_other_4567,public", false)]
    public void SearchPath_AcceptsSchemasConfiguredAsOperational(string searchPath, bool allowed)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:OperationalSchemas:0"] = "test_fixture_0123"
            })
            .Build();

        var policy = PostgresSchemaConfiguration.FromConfiguration(configuration);

        Assert.Equal(allowed, policy.IsAllowedSearchPath(searchPath));
    }
}
