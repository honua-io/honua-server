// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;

namespace Honua.Server.Tests;

public sealed partial class FeatureServerEndpointTests
    : IClassFixture<FeatureServerEndpointTests.QueryApplicationFixture>, IAsyncLifetime
{
    private WebAppFixture _fixture;
    private WebAppFixture? _isolatedApplication;

    public FeatureServerEndpointTests(QueryApplicationFixture queryApplication)
    {
        _fixture = queryApplication.Application;
    }

    // Metadata and query cases share one seeded Pro application for the class; they must not
    // write features or metadata. Cases that edit, reseed or mutate metadata call
    // UseIsolatedApplicationAsync for their own application and schema.
    public sealed class QueryApplicationFixture : IAsyncLifetime
    {
        public WebAppFixture Application { get; } = CreateApplication();

        public Task InitializeAsync() => InitializeApplicationAsync(Application);

        public Task DisposeAsync() => Application.DisposeAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _isolatedApplication?.DisposeAsync() ?? Task.CompletedTask;

    private static WebAppFixture CreateApplication() => new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    private static async Task InitializeApplicationAsync(WebAppFixture application)
    {
        await application.InitializeAsync();
        application.EnableV2ServiceEditingCapabilities(TestServiceId, ["Create", "Update", "Delete"]);
    }

    private async Task UseIsolatedApplicationAsync()
    {
        _isolatedApplication = CreateApplication();
        await InitializeApplicationAsync(_isolatedApplication);
        _fixture = _isolatedApplication;
    }
}
