// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class AdminConnectImportMultipartTests
{
    // The loopback upload must carry a part MIME type, or upload validation refuses the file.
    [UnitTest]
    public void BuildMultipart_FilePart_HasAnAllowedMimeTypeAndTheCallerFileName()
    {
        var request = new OperationRequest
        {
            OperationId = "admin.import.preview",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["file"] = Convert.ToBase64String("{}"u8.ToArray()),
                ["fileName"] = "parcels.geojson",
                ["dryRun"] = "true",
            },
        };

        using var content = AdminConnectImportOperationExecutor.BuildMultipart(request, []);

        var file = content.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "file");
        file.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        file.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("parcels.geojson");
        content.Should().NotContain(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "fileName");
    }
}
