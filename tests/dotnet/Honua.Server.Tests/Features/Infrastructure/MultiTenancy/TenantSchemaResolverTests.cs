// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.MultiTenancy;

public class TenantSchemaResolverTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("acme-east", "acme_east")]
    [InlineData("acme.east", "acme:east")]
    [InlineData("Acme", "acme")]
    [InlineData("acme ", "acme")]
    public void DistinctTenantIds_MustNotShareDerivedSchema(string first, string second)
    {
        var resolver = Create(new TenantSchemaOptions());
        var firstResolved = resolver.TryResolveSchema(first, out var firstSchema);
        var secondResolved = resolver.TryResolveSchema(second, out var secondSchema);

        Assert.False(firstResolved && secondResolved
            && string.Equals(firstSchema, secondSchema, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("tenant_shared", "tenant_shared")]
    [InlineData("tenant_shared", "TENANT_SHARED")]
    public void DuplicateMappings_RejectBothOwners(string firstSchema, string secondSchema)
    {
        var resolver = Create(new TenantSchemaOptions
        {
            SchemaMap = new(StringComparer.Ordinal)
            {
                ["first"] = firstSchema,
                ["second"] = secondSchema,
            },
        });

        Assert.False(resolver.TryResolveSchema("first", out _));
        Assert.False(resolver.TryResolveSchema("second", out _));
    }

    [UnitTest]
    public void ExistingMapping_PreservesSchemaAndReservesItFromDerivedTenant()
    {
        var resolver = Create(new TenantSchemaOptions
        {
            SchemaMap = new(StringComparer.Ordinal) { ["acme-east"] = "tenant_acme_east" },
        });

        Assert.True(resolver.TryResolveSchema("acme-east", out var schema));
        Assert.Equal("tenant_acme_east", schema);
        Assert.False(resolver.TryResolveSchema("acme_east", out _));
        Assert.False(resolver.TryResolveSchema("ACME-EAST", out _));
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("bad-name")]
    [InlineData(" tenant_good ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void InvalidMapping_DoesNotFallBackToDerivedSchema(string mapped)
    {
        var resolver = Create(new TenantSchemaOptions
        {
            SchemaMap = new(StringComparer.Ordinal) { ["acme"] = mapped },
        });

        Assert.False(resolver.TryResolveSchema("acme", out _));
    }

    private static TenantSchemaResolver Create(TenantSchemaOptions options) => new(Options.Create(options));
}
