// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Migration;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class ImportValidationErrorMessageTests
{
    [UnitTest]
    public void FromArgument_WithUrlInMessage_RedactsUrl()
    {
        var exception = new ArgumentException(
            "Service URL https://user:password@example.internal/wfs?token=secret is not reachable.");

        var message = ImportValidationErrorMessage.FromArgument(exception, "Invalid import request.");

        message.Should().Contain("[redacted-url]");
        message.Should().NotContain("password");
        message.Should().NotContain("token=secret");
    }
}
