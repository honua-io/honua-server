// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Io.Export;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Export;

public sealed class ExportScratchTests
{
    [UnitTest]
    public void Create_RunDirectory_IsOwnerOnlyOnUnix()
    {
        var parent = Path.Join(Path.GetTempPath(), "honua-export-scratch-test-" + Guid.NewGuid().ToString("N"));
        var scratchDir = Path.Join(parent, Guid.NewGuid().ToString("N"));
        try
        {
            ExportScratch.Create(scratchDir);

            Directory.Exists(scratchDir).Should().BeTrue();
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(scratchDir).Should().Be(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
