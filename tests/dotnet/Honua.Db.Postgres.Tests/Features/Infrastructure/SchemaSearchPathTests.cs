// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

public sealed class SchemaSearchPathTests
{
    [Theory]
    [InlineData("tenant_42", "\"tenant_42\"")]
    [InlineData("select", "\"select\"")]
    [InlineData("MixedCase", "\"MixedCase\"")]
    public void ValidateAndQuote_UsesPostgresIdentifierQuoting(string schemaName, string expected)
    {
        SchemaSearchPath.ValidateAndQuote(schemaName).Should().Be(expected);
    }

    [Theory]
    [InlineData("tenant\"; DROP SCHEMA public; --")]
    [InlineData("tenant, public")]
    [InlineData("tenant.name")]
    public void ValidateAndQuote_RejectsInputThatCouldChangeSearchPath(string schemaName)
    {
        var action = () => SchemaSearchPath.ValidateAndQuote(schemaName);

        action.Should().Throw<InvalidOperationException>();
    }
}
