// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class Wfs20EndpointsTests : IClassFixture<Wfs20EndpointsTests.Fixture>
{
    private readonly WebAppFixture _fixture;

    public Wfs20EndpointsTests(Fixture fixture)
    {
        _fixture = fixture.Application;
    }

    // Read-only cases share one seeded application for this sequential class. Cases that run
    // transactions, manage stored queries, or write features or metadata start their own.
    // Managed stored queries are process-wide, so no case may assert an exact stored-query count.
    public sealed class Fixture : IAsyncLifetime
    {
        public WebAppFixture Application { get; } = CreateApplication();

        public Task InitializeAsync() => Application.InitializeAsync();

        public Task DisposeAsync() => Application.DisposeAsync();
    }

    private static WebAppFixture CreateApplication() =>
        new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    private static async Task<WebAppFixture> StartIsolatedApplicationAsync()
    {
        var application = CreateApplication();
        try
        {
            await application.InitializeAsync();
            return application;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> GetTestLayerFeatureByIdAsync(WebAppFixture application, long featureId)
    {
        var response = await application.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&RESOURCEID=test_layer.{featureId}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }
}
