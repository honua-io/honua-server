// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

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

    [Fact]
    public void BuildSearchPathValue_QuotesSchemaAndKeepsPublicFallback()
    {
        SchemaSearchPath.BuildSearchPathValue("select").Should().Be("\"select\", public");
    }

    [Theory]
    [InlineData("tenant\n")]
    [InlineData(" tenant")]
    [InlineData("tenant\"; SET search_path = public; --")]
    [InlineData("tenant, public")]
    public async Task ApplyAsync_RejectsUnsafeIdentityBeforeDefaultSchemaShortcut(string schemaName)
    {
        await using var connection = new NpgsqlConnection();
        var action = () => SchemaSearchPath.ApplyAsync(connection, schemaName, schemaName);

        await action.Should().ThrowAsync<InvalidOperationException>();
        SchemaSearchPath.IsValidIdentifier(schemaName).Should().BeFalse();
        var quote = () => SchemaSearchPath.ValidateAndQuote(schemaName);
        quote.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ApplyAsync_RejectsIdentifiersThatPostgresWouldTruncateOntoAnotherSchema()
    {
        var schemaName = new string('a', 63);
        SchemaSearchPath.IsValidIdentifier(schemaName).Should().BeTrue();
        SchemaSearchPath.IsValidIdentifier(schemaName + "b").Should().BeFalse();
        await using var connection = new NpgsqlConnection();
        var action = () => SchemaSearchPath.ApplyAsync(connection, schemaName + "b", schemaName + "b");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }
}
