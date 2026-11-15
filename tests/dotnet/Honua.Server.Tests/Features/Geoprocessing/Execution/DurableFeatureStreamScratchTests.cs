// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class DurableFeatureStreamTests
{
    [UnitTest]
    public async Task ManagedRead_KeepsVerifiedScratchPrivateAndDeletesItAfterEnumeration()
    {
        var produced = await Produce();
        string path;
        await using (var rows = DurableFeatureStream.ReadRowsAsync(
            produced.Reference, _records["producer"], _jobs, NewStore(), _staging).GetAsyncEnumerator())
        {
            (await rows.MoveNextAsync()).Should().BeTrue();
            var scratch = rows.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(field => field.GetValue(rows)).OfType<FileStream>()
                .Where(stream => stream.CanWrite).Should().ContainSingle().Subject;
            path = scratch.Name;
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(PrivateTempFile.OwnerReadWrite);
            }
            (await rows.MoveNextAsync()).Should().BeFalse();
        }
        File.Exists(path).Should().BeFalse();
    }
}
