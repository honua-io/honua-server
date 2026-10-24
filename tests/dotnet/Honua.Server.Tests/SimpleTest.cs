// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests;

/// <summary>
/// Simple validation test without Docker dependencies
/// </summary>
public class SimpleTest
{
    [UnitTest]
    public void AttributeFramework_Works()
    {
        // Assert
        true.Should().BeTrue("Unit test framework should work");
    }

    [UnitTest]
    public void FluentAssertions_Works()
    {
        // Arrange
        var testValue = "Hello World";

        // Assert
        testValue.Should().NotBeNull();
        testValue.Should().Contain("World");
    }
}
