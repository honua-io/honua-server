// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class ResponseCompressionMimeTypeTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ConfigureResponseCompression_RegistersVectorAndTileMimeTypes()
    {
        var services = new ServiceCollection();

        ObservabilityServiceCollectionExtensions.ConfigureResponseCompression(services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value;
        var mimeTypes = options.MimeTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        mimeTypes.Should().Contain("application/geo+json");
        mimeTypes.Should().Contain("application/vnd.mapbox-vector-tile");
        mimeTypes.Should().Contain("application/x-protobuf");
        mimeTypes.Should().Contain("application/gml+xml");
        mimeTypes.Should().Contain("application/flatgeobuf");
        mimeTypes.Should().Contain("application/vnd.apache.arrow.stream");
    }
}
