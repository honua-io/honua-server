// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

public sealed class ImageServerSoapExportedImageTests
{
    private static readonly byte[] Encoded = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00];

    [UnitTest]
    [Operation(Operations.Export)]
    public void GetExportedImageBytes_EncodedRasterStreamResult_ReturnsTheBytes()
    {
        var result = EncodedRasterResults.File(Encoded, "image/tiff");

        var imageData = ImageServerSoapEndpoints.GetExportedImageBytes(result);

        imageData.Should().NotBeNull();
        imageData!.Value.ToArray().Should().Equal(Encoded);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void GetExportedImageBytes_ByteResult_ReturnsTheBytes()
    {
        var result = Results.File(Encoded, "image/tiff");

        var imageData = ImageServerSoapEndpoints.GetExportedImageBytes(result);

        imageData.Should().NotBeNull();
        imageData!.Value.ToArray().Should().Equal(Encoded);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void GetExportedImageBytes_ProblemResult_ReturnsNull()
    {
        ImageServerSoapEndpoints.GetExportedImageBytes(Results.BadRequest()).Should().BeNull();
    }
}
