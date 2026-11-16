// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.OData;

// One Pro-licensed, odata.yaml-seeded application. As a class fixture it serves a class's
// read-only cases; StartPrivateAsync gives each mutating case its own host and schema.
public sealed class ODataProHost : IAsyncLifetime
{
    public WebAppFixture Fixture { get; } = CreateFixture();

    public Task InitializeAsync() => Fixture.InitializeAsync();

    public Task DisposeAsync() => Fixture.DisposeAsync();

    public static async Task<WebAppFixture> StartPrivateAsync()
    {
        var fixture = CreateFixture();
        try
        {
            await fixture.InitializeAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    // All segments are relative literal path fragments (not user input), so none can be
    // rooted and silently drop earlier arguments.
    private static WebAppFixture CreateFixture()
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro).UseSeed(Path.Join("tests", "seed", "odata.yaml"));
}
