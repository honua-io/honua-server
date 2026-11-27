// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class VaryHeaderTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public void AppendMissing_LongerHeaderAlreadyPresent_StillAddsShorterToken()
    {
        var headers = new HeaderDictionary { [HeaderNames.Vary] = "Accept-Encoding" };

        VaryHeader.AppendMissing(headers, HeaderNames.Accept, HeaderNames.AcceptLanguage);

        headers.GetCommaSeparatedValues(HeaderNames.Vary).Should().Equal("Accept-Encoding", "Accept", "Accept-Language");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void AppendMissing_ListAndRepeats_AddsEachTokenOnceIgnoringCase()
    {
        var headers = new HeaderDictionary { [HeaderNames.Vary] = "authorization" };

        VaryHeader.AppendMissing(headers, "Authorization, X-API-Key", "x-api-key", "Origin");

        headers.GetCommaSeparatedValues(HeaderNames.Vary).Should().Equal("authorization", "X-API-Key", "Origin");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void AppendMissing_WildcardPresent_AddsNothing()
    {
        var headers = new HeaderDictionary { [HeaderNames.Vary] = "*" };

        VaryHeader.AppendMissing(headers, HeaderNames.AcceptEncoding);

        headers.GetCommaSeparatedValues(HeaderNames.Vary).Should().Equal("*");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IdentityCodingPolicy_AfterNegotiatedCoding_KeysEveryCodingAsIdentity()
    {
        var context = new OutputCacheContext { HttpContext = new DefaultHttpContext() };
        context.CacheVaryByRules.VaryByValues[OutputCachingRegistration.ContentCodingKey] = "br";

        await new IdentityCodingOutputCachePolicy().CacheRequestAsync(context, CancellationToken.None);

        context.CacheVaryByRules.VaryByValues[OutputCachingRegistration.ContentCodingKey]
            .Should().Be(OutputCachingRegistration.IdentityCoding);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task MvtTileAndH3MvtTilePolicies_PreserveNegotiatedContentCoding()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new Honua.Core.Configuration.LimitsOptions()));
        var configuration = new ConfigurationBuilder().Build();
        OutputCachingRegistration.ConfigureOutputCaching(services, configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OutputCacheOptions>>().Value;

        var namedPoliciesField = typeof(OutputCacheOptions).GetProperty(
            "NamedPolicies",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var namedPolicies = (IDictionary<string, IOutputCachePolicy>)namedPoliciesField.GetValue(options)!;

        foreach (var policyName in new[] { "MvtTile", "H3MvtTile" })
        {
            var context = new OutputCacheContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = provider }
            };
            context.CacheVaryByRules.VaryByValues[OutputCachingRegistration.ContentCodingKey] = "br";

            await namedPolicies[policyName].CacheRequestAsync(context, CancellationToken.None);

            context.CacheVaryByRules.VaryByValues[OutputCachingRegistration.ContentCodingKey]
                .Should().Be("br", $"{policyName} must vary by negotiated content-coding now that MVT compression is enabled");
        }
    }
}
