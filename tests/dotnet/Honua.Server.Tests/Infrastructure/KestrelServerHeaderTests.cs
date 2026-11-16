// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure;

[Protocol(TestProtocols.Admin)]
public sealed class KestrelServerHeaderTests
{
    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    public void ProductionHost_DoesNotAdvertiseTheServerImplementation()
    {
        using var factory = new TestWebApplicationFactory();

        var options = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        options.AddServerHeader.Should().BeFalse();
    }
}
