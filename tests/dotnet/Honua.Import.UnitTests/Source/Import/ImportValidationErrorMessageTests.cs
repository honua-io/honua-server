// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
