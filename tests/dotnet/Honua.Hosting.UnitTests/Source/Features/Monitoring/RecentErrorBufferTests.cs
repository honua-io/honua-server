// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Honua.Hosting.UnitTests.Source.Features.Monitoring;

public sealed class RecentErrorBufferTests
{
    [UnitTheory]
    [InlineData("/api/v1/attachments/downloads/signed-token-value", "/api/v1/attachments/downloads/[REDACTED]")]
    [InlineData("/api/v1.0/attachments/downloads/signed-token-value", "/api/v1.0/attachments/downloads/[REDACTED]")]
    [InlineData("/api/v1/console/share/link/share-token-value", "/api/v1/console/share/link/[REDACTED]")]
    [InlineData("/wps/conformance/results/job-token-value", "/wps/conformance/results/[REDACTED]")]
    [InlineData("/api/v1/tiles/1/2/3.mvt", "/api/v1/tiles/1/2/3.mvt")]
    public void Record_RedactsCredentialBearingRequestPaths(string requestPath, string expectedStoredPath)
    {
        var buffer = new RecentErrorBuffer(Options.Create(new RecentErrorBufferOptions { Capacity = 5 }));
        var context = new DefaultHttpContext { TraceIdentifier = "trace-p0-2" };
        context.Request.Path = requestPath;

        buffer.Record(context, StatusCodes.Status500InternalServerError, "Internal Server Error", "Failure");

        var entry = buffer.Snapshot().Should().ContainSingle().Subject;
        entry.Path.Should().Be(expectedStoredPath);
    }
}
