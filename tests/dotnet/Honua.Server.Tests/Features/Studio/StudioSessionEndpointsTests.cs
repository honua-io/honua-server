// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Studio;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Studio;

public sealed class StudioSessionEndpointsTests
{
    [UnitTheory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("prod-us_1", "prod-us_1")]
    [InlineData("prod/us", null)]
    [InlineData("prod us", null)]
    public void ResolveEnvironmentId_UsesValidatedConfiguration(string? configured, string? expected)
    {
        StudioSessionEndpoints.ResolveEnvironmentId(Configuration(StudioSessionEndpoints.EnvironmentIdKey, configured))
            .Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(null, "", "")]
    [InlineData(null, "/honua", "/honua")]
    [InlineData("/gis/api/", "/honua", "/gis/api")]
    [InlineData("/", "", "")]
    [InlineData("gis", "", null)]
    [InlineData("//evil.example", "", null)]
    [InlineData("/gis?x=1", "", null)]
    [InlineData("/gis#frag", "", null)]
    [InlineData("/gis\\api", "", null)]
    [InlineData("/gis api", "", null)]
    public void ResolveSourcePathBase_PrefersConfigurationThenRequestPathBase(
        string? configured, string requestPathBase, string? expected)
    {
        StudioSessionEndpoints.ResolveSourcePathBase(
                Configuration(StudioSessionEndpoints.SourcePathBaseKey, configured),
                new PathString(requestPathBase.Length == 0 ? null : requestPathBase))
            .Should().Be(expected);
    }

    private static IConfiguration Configuration(string key, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
}
