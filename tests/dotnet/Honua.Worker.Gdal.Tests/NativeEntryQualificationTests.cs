// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

/// <summary>Pins the opt-in boundary: ordinary execution never acquires loader or handshake settings.</summary>
public sealed class NativeEntryQualificationTests
{
    [UnitTest]
    public void Prepare_ProductionDefault_PreservesExecutableArgumentsAndEnvironment()
    {
        var original = Environment.GetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable, null);
            var start = new ProcessStartInfo("ogr2ogr");
            start.ArgumentList.Add("--version");
            var environment = start.Environment.ToArray();
            Assert.Null(NativeEntryQualification.Prepare(start));
            Assert.Equal("ogr2ogr", start.FileName);
            Assert.Equal(["--version"], start.ArgumentList);
            Assert.Equal(environment, start.Environment.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable, original);
        }
    }

    [UnitTest]
    public async Task RunProcessAsync_MissingHandshake_KillsAndReapsActualNativeChild()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // The Linux /proc and ELF boundary is exercised by the Linux worker gate.
        }
        var directory = Path.Join(Path.GetTempPath(), $"honua-entry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var child = new Process { StartInfo = new ProcessStartInfo("/bin/sleep", "60")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            var handshake = new NativeEntryQualification(Path.Join(directory, "missing.json"),
                Path.Join(directory, "release"), new string('b', 32), "/usr/bin/sleep", TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ProcessGdalCommandRunner.RunProcessAsync(child, handshake, CancellationToken.None));
            Assert.True(child.HasExited);
            Assert.False(Directory.Exists($"/proc/{child.Id}"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnitTest]
    public async Task RunProcessAsync_InvalidHandshakeIo_KillsAndReapsActualNativeChild()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var directory = Path.Join(Path.GetTempPath(), $"honua-entry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var record = Path.Join(directory, "entry.json");
            await File.WriteAllTextAsync(record, "{}");
            using var child = new Process { StartInfo = new ProcessStartInfo("/bin/sleep", "60")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            var handshake = new NativeEntryQualification(record, Path.Join(directory, "release"),
                new string('b', 32), "/usr/bin/sleep");
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                ProcessGdalCommandRunner.RunProcessAsync(child, handshake, CancellationToken.None));
            Assert.True(child.HasExited);
            Assert.False(Directory.Exists($"/proc/{child.Id}"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    [UnitTest]
    public void Release_PublishesCompleteNonceAndRefusesStaleDestinationWithoutReplacingIt()
    {
        var directory = Path.Join(Path.GetTempPath(), $"honua-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var release = Path.Join(directory, "release");
            var nonce = new string('b', 32);
            var gate = new NativeEntryQualification(Path.Join(directory, "entry"), release, nonce, "/usr/bin/ogr2ogr");
            gate.Release();
            Assert.Equal(nonce, File.ReadAllText(release));
            Assert.Single(Directory.GetFiles(directory));
            File.WriteAllText(release, "stale");
            Assert.Throws<IOException>(gate.Release);
            Assert.Equal("stale", File.ReadAllText(release));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

}
