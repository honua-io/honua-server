// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationLinkedUrlPolicyTests
{
    [Fact]
    public void Allows_NullAllowlist_AcceptsHttpsAndRejectsHttp()
    {
        var policy = new MigrationLinkedUrlPolicy(false, null);

        policy.Allows(new Uri("https://demo.example/items")).Should().BeTrue();
        policy.Allows(new Uri("http://demo.example/items")).Should().BeFalse();
    }

    [Fact]
    public void Allows_EmptyAllowlist_RejectsEveryHost()
    {
        var policy = new MigrationLinkedUrlPolicy(false, []);

        policy.Allows(new Uri("https://demo.example/items")).Should().BeFalse();
    }

    [Fact]
    public void EnsureAllows_OffHostNextLink_Throws()
    {
        var policy = new MigrationLinkedUrlPolicy(false, ["demo.example"]);

        var act = () => policy.EnsureAllows(new Uri("https://evil.example/items"));

        act.Should().Throw<HttpRequestException>();
    }

    [Fact]
    public void Allows_SuffixMatch_AcceptsChildHost()
    {
        var policy = new MigrationLinkedUrlPolicy(true, ["example"]);

        policy.Allows(new Uri("http://tiles.example/wmts")).Should().BeTrue();
        policy.Allows(new Uri("https://evil.test/wmts")).Should().BeFalse();
    }
}
