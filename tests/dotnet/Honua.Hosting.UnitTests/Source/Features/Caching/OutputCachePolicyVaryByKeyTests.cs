// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Hosting.UnitTests.Features.Caching;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class OutputCachePolicyVaryByKeyTests
{
    [UnitTest]
    public async Task MapServerQueryDomainsPolicy_VariesByServiceIdFormatAndLayers()
    {
        var context = await EvaluatePolicyAsync("MapServerQueryDomains");

        context.CacheVaryByRules.RouteValueNames.ToArray().Should().Contain("serviceId");
        context.CacheVaryByRules.QueryKeys.ToArray().Should().Contain(["f", "layers"]);
    }

    [UnitTest]
    public async Task OgcTilesTilesetsPolicy_VariesByFormatAndCollections()
    {
        var context = await EvaluatePolicyAsync("OgcTilesTilesets");

        context.CacheVaryByRules.QueryKeys.ToArray().Should().Contain(["f", "collections"]);
    }

    private static async Task<OutputCacheContext> EvaluatePolicyAsync(string policyName)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        OutputCachingRegistration.ConfigureOutputCaching(services, configuration);

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OutputCacheOptions>>().Value;
        var namedPolicies = (IDictionary<string, IOutputCachePolicy>)typeof(OutputCacheOptions)
            .GetProperty("NamedPolicies", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(options)!;

        namedPolicies.Should().ContainKey(policyName);
        var cacheContext = new OutputCacheContext { HttpContext = new DefaultHttpContext() };
        await namedPolicies[policyName].CacheRequestAsync(cacheContext, CancellationToken.None);
        return cacheContext;
    }
}
