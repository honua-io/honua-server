// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.MultiTenancy;

public class TenantSchemaEncodingTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("acme-east", "tenant_acme_002deast")]
    [InlineData("acme_east", "tenant_acme_005feast")]
    [InlineData("acme:east", "tenant_acme_003aeast")]
    [InlineData("Acme", "tenant__0041cme")]
    public void EncodedRouting_UsesReversibleNames(string tenantId, string expected)
    {
        var resolver = Create(new TenantSchemaOptions { UseEncodedSchemaNames = true });

        Assert.True(resolver.TryResolveSchema(tenantId, out var schema));
        Assert.Equal(expected, schema);
        Assert.True(Create(new TenantSchemaOptions { UseEncodedSchemaNames = true })
            .TryResolveSchema(tenantId, out var afterRestart));
        Assert.Equal(schema, afterRestart);
    }

    [UnitTest]
    public void EncodedRouting_EscapesEveryAmbiguousCharacterIncludingEscapeCharacter()
    {
        const string characters = "azAZ09_-.@:";
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolver = Create(new TenantSchemaOptions { UseEncodedSchemaNames = true });
        foreach (var first in characters)
        {
            foreach (var second in characters)
            {
                Assert.True(resolver.TryResolveSchema($"{first}{second}", out var schema));
                Assert.True(schemas.Add(schema), $"Duplicate schema for {first}{second}");
            }
        }
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentifierLimit_RejectsInsteadOfTruncating(bool encoded)
    {
        var resolver = Create(new TenantSchemaOptions { UseEncodedSchemaNames = encoded });
        Assert.True(resolver.TryResolveSchema(new string('a', 56), out var schema));
        Assert.Equal(63, schema.Length);
        Assert.False(resolver.TryResolveSchema(new string('a', 57), out _));
        Assert.False(resolver.TryResolveSchema(new string('_', 57), out _));
    }

    [UnitTest]
    public void ConflictingTenantDeclarations_FailClosedAndReserveBothSchemas()
    {
        var resolver = Create(new TenantSchemaOptions
        {
            SchemaMap = new() { ["owner"] = "tenant_first" },
            SchemaMappings = [new() { TenantId = "owner", SchemaName = "tenant_second" }],
        });

        Assert.False(resolver.TryResolveSchema("owner", out _));
        Assert.False(resolver.TryResolveSchema("first", out _));
        Assert.False(resolver.TryResolveSchema("second", out _));
    }

    private static TenantSchemaResolver Create(TenantSchemaOptions options) => new(Options.Create(options));
}
