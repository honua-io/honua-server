// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Geoprocessing.Tests;

public sealed class SpillableKeySetTests
{
    [UnitTest]
    public void Add_WhenSpilled_KeepsScratchPrivateAndPreservesDeduplication()
    {
        string path;
        using (var keys = new SpillableKeySet(inMemoryDigestLimit: 1, windowLimit: 1))
        {
            keys.Add("first").Should().BeTrue();
            var field = typeof(SpillableKeySet).GetField("_spillFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stream = field.GetValue(keys).Should().BeOfType<FileStream>().Subject;
            path = stream.Name;
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(PrivateTempFile.OwnerReadWrite);
            }

            keys.Add("second").Should().BeTrue();
            keys.Add("first").Should().BeFalse();
            keys.Add("second").Should().BeFalse();
        }

        File.Exists(path).Should().BeFalse();
    }
}
