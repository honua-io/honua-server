// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class ExternalRevisionOutputCacheTests
{
    // Cross-host coherence against one database is covered by MultiTaskCacheCoherenceTests; this pins the key derivation.
    [UnitTest]
    public async Task SameRevision_GivesHostsTheSameKey_AndANewRevisionChangesIt()
    {
        var source = new Revisions();
        using var first = new ServiceCollection().AddSingleton<IExternalLayerRevisionSource>(source).BuildServiceProvider();
        using var second = new ServiceCollection().AddSingleton<IExternalLayerRevisionSource>(source).BuildServiceProvider();
        var beforeFirst = await KeyAsync(first);
        var beforeSecond = await KeyAsync(second);

        source.Value = "second";

        beforeFirst.Should().Be(beforeSecond);
        (await KeyAsync(first)).Should().NotBe(beforeFirst);
        (await KeyAsync(second)).Should().Be(await KeyAsync(first));
    }

    [UnitTest]
    public async Task RevisionReadFailure_PreventsCacheLookup()
    {
        using var services = new ServiceCollection().AddSingleton<IExternalLayerRevisionSource>(new Revisions { Fail = true })
            .BuildServiceProvider();
        var lookup = () => KeyAsync(services);

        await lookup.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<string> KeyAsync(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "GET";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new OutputCacheAttribute()), "tiles"));
        var context = new OutputCacheContext { HttpContext = http };
        await new RouteTagOutputCachePolicy().CacheRequestAsync(context, CancellationToken.None);
        return context.CacheVaryByRules.VaryByValues["honua-external-generation"].ToString();
    }

    private sealed class Revisions : IExternalLayerRevisionSource
    {
        public string Value { get; set; } = "first";
        public bool Fail { get; set; }
        public Task<string> GetRevisionAsync(CancellationToken cancellationToken = default)
            => Fail ? Task.FromException<string>(new InvalidOperationException("unavailable")) : Task.FromResult(Value);
    }
}
