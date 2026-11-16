// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// scripts/ci/uncalled-method-ratchet.py lists methods whose name appears only at their declaration
// across the repository's code and configuration text.
[Trait("Category", "Architecture")]
public sealed class UncalledMethodRatchetTests
{
    private const string RelativeScript = "scripts/ci/uncalled-method-ratchet.py";

    [ArchitectureTest]
    public async Task Scanner_SelfTest_Passes()
    {
        var (exitCode, _, error) = await RepositoryPythonScript.RunAsync(RelativeScript, "--self-test");
        exitCode.Should().Be(0, "the uncalled-method scanner self-test failed: {0}", error);
    }

    [ArchitectureTest]
    public async Task Source_HasNoUncalledMethods()
    {
        var (exitCode, output, error) = await RepositoryPythonScript.RunAsync(RelativeScript);
        exitCode.Should().Be(0, "the uncalled-method scanner failed: {0}", error);
        output.Trim().Should().BeEmpty("these methods have no caller in the repository; check for a reflective or external caller, then delete them");
    }
}
