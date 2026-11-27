// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Logging;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Logging;

public sealed class CredentialPathRedactionTests
{
    [UnitTheory]
    [InlineData("/api/v1/console/share/link/opaque-token", "/api/v1/console/share/link/[REDACTED]")]
    [InlineData("/api/v1.0/console/share/embed/embed-token?x=1", "/api/v1.0/console/share/embed/[REDACTED]?x=1")]
    [InlineData("/api/v1/attachments/downloads/signed-bearer-token", "/api/v1/attachments/downloads/[REDACTED]")]
    [InlineData("/api/v1.0/attachments/downloads/signed-bearer-token?download=1", "/api/v1.0/attachments/downloads/[REDACTED]?download=1")]
    [InlineData("https://example.test/api/v1/attachments/downloads/signed-bearer-token", "https://example.test/api/v1/attachments/downloads/[REDACTED]")]
    [InlineData("/wps/conformance/results/job-secret", "/wps/conformance/results/[REDACTED]")]
    [InlineData("/tiles/7/0/0/0.mvt", "/tiles/7/0/0/0.mvt")]
    public void Issue5635_Redact_ScrubsCapabilityTokenPaths(string input, string expected)
    {
        CredentialPathRedaction.Redact(input).Should().Be(expected);
    }
}
