// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.UnitTests;

public sealed class GeoServicesPortalUrlResolverTests
{
    [UnitTheory]
    [InlineData("https://public.example", "/arcgis", "https://public.example")]
    [InlineData("https://public.example/enterprise/", "/arcgis", "https://public.example/enterprise")]
    [InlineData("https://public.example/arcgis", "", "https://public.example/arcgis")]
    [InlineData(null, "", "https://localhost:18771")]
    [InlineData(null, "/mounted", "https://localhost:18771/mounted")]
    public void GetBaseUrl_PreservesActualPublicPortalWithoutAddingSoapAlias(
        string? configured, string pathBase, string expected)
    {
        // A nonabsolute setting selects the fallback without inheriting an operator env URL.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Public:BaseUrl"] = configured ?? "not-absolute" }).Build();
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("untrusted.example", 9999);
        context.Connection.LocalPort = 18771;
        context.Request.PathBase = pathBase;

        GeoServicesPortalUrlResolver.GetBaseUrl(context).Should().Be(expected);
    }
}
