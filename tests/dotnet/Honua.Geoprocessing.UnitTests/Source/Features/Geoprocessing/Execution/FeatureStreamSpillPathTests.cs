// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class FeatureStreamSpillPathTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "honua-spill-test-" + Guid.NewGuid().ToString("N"));

    [UnitTest]
    public void AllocateSpillPath_FreshRoot_CreatesOwnerOnlyStreamDirectory()
    {
        var path = FeatureStreamArtifact.AllocateSpillPath(_root, "op", "proc");

        var streams = Path.GetDirectoryName(path)!;
        Directory.Exists(streams).Should().BeTrue();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(streams).Should().Be(PrivateTempFile.OwnerDirectory);
        }
    }

    [UnitTest]
    public void AllocateSpillPath_SharedStreamDirectory_BecomesOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var streams = Path.Join(_root, "streams");
        Directory.CreateDirectory(streams);
        File.SetUnixFileMode(streams, PrivateTempFile.OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.OtherWrite);

        FeatureStreamArtifact.AllocateSpillPath(_root, "op", "proc");

        File.GetUnixFileMode(streams).Should().Be(PrivateTempFile.OwnerDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
