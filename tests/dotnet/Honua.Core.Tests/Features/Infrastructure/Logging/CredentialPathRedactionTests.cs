// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Logging;

namespace Honua.Core.Tests.Features.Infrastructure.Logging;

public sealed class CredentialPathRedactionTests
{
    [Theory]
    [InlineData("/api/v1/console/share/link/opaque-token", "/api/v1/console/share/link/[REDACTED]")]
    [InlineData("/api/v1.0/console/share/embed/embed-token?x=1", "/api/v1.0/console/share/embed/[REDACTED]?x=1")]
    [InlineData("/wps/conformance/results/job-secret", "/wps/conformance/results/[REDACTED]")]
    [InlineData("/tiles/7/0/0/0.mvt", "/tiles/7/0/0/0.mvt")]
    public void Issue5635_Redact_ScrubsCapabilityTokenPaths(string input, string expected)
    {
        CredentialPathRedaction.Redact(input).Should().Be(expected);
    }
}
