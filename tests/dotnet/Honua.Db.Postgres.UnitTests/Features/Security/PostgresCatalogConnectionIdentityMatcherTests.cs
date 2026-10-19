// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Security;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Security;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class PostgresCatalogConnectionIdentityMatcherTests
{
    private const string Catalog =
        "Host=localhost;Database=honua_test;Username=honua;Password=\"secret;with=punctuation\";Application Name=honua-mcp-cert-1";

    private readonly PostgresCatalogConnectionIdentityMatcher _matcher = new();

    [SecurityTest]
    [Fact]
    public void SupportedAdminRegistration_MatchesCatalogAcrossAliasesDefaultsAndSessionOptions()
    {
        // The production admin API's encrypted registration builder emits Port
        // and SSL Mode; the server's catalog string may omit both and include
        // a run-owned Application Name. The credentials are deliberately not
        // echoed or compared as connection identity.
        const string registered =
            "Server=LOCALHOST;Port=5432;Database=honua_test;User ID=honua;Password=other;SSL Mode=Disable";

        Assert.True(_matcher.IsMatch("postgis", Catalog, registered));
    }

    [SecurityTest]
    [Fact]
    public void QuotedValues_WithSemicolons_AreParsedWithoutLosingIdentity()
    {
        const string catalog =
            "Host=db.example;Database=\"Work;Zone\";Username=\"Case;Owner\";Application Name=\"a;b\"";
        const string registered =
            "Server=DB.EXAMPLE;Port=5432;Database=\"Work;Zone\";User ID=\"Case;Owner\";SSL Mode=Disable";

        Assert.True(_matcher.IsMatch("postgis", catalog, registered));
    }

    [SecurityTest]
    [Theory]
    [InlineData("Host=elsewhere;Database=honua_test;Username=honua", "postgis")]
    [InlineData("Host=localhost;Port=5433;Database=honua_test;Username=honua", "postgis")]
    [InlineData("Host=localhost;Database=HONUA_TEST;Username=honua", "postgis")]
    [InlineData("Host=localhost;Database=honua_test;Username=HONUA", "postgis")]
    [InlineData("Host=localhost;Database=honua_test;Username=honua", "mysql")]
    [InlineData("Host=localhost;Database=honua_test;Username=honua", "postgresql")]
    [InlineData("Host=localhost;Database=honua_test", "postgis")]
    [InlineData("Host=localhost;Username=honua", "postgis")]
    [InlineData("Host=localhost,standby;Database=honua_test;Username=honua", "postgis")]
    [InlineData("Host=/var/run/postgresql;Database=honua_test;Username=honua", "postgis")]
    [InlineData("Host=localhost;Database=honua_test;Username=honua;UnsupportedKeyword=bad", "postgis")]
    public void DistinctOrUnprovableTarget_DoesNotProduceAConnectionHint(string registered, string provider)
    {
        Assert.False(_matcher.IsMatch(provider, Catalog, registered));
    }

    [SecurityTest]
    [Fact]
    public void MalformedCatalogConnection_DoesNotProduceAConnectionHint()
    {
        Assert.False(_matcher.IsMatch("postgis", "not-a-connection-string", Catalog));
    }
}
