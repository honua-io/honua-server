// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.LicenseMint;
using Honua.TestKit.Attributes;

namespace Honua.LicenseMint.Tests;

/// <summary>
/// Proves <c>keygen --private-out</c> creates a mode-0600 seed and refuses to replace it.
/// </summary>
public sealed class LicenseMintKeygenTests
{
    private static readonly object ConsoleLock = new();

    [UnitTest]
    public void Keygen_PrivateOut_RefusesToOverwriteWithoutForce()
    {
        var path = Path.Combine(Path.GetTempPath(), "honua-license-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Run("keygen", "--private-out", path, "--key-id", "honua-test");
            var original = File.ReadAllBytes(path);

            var second = Run("keygen", "--private-out", path, "--key-id", "honua-test");
            var after = File.ReadAllBytes(path);

            first.Should().Be(0);
            second.Should().NotBe(0);
            after.Should().Equal(original);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [UnitTest]
    public void Keygen_Force_ReplacesTheExistingSeed()
    {
        var path = Path.Combine(Path.GetTempPath(), "honua-license-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            Run("keygen", "--private-out", path, "--key-id", "honua-test").Should().Be(0);
            var original = File.ReadAllBytes(path);

            var replaced = Run("keygen", "--force", "--private-out", path, "--key-id", "honua-test");
            var after = File.ReadAllBytes(path);

            replaced.Should().Be(0);
            after.Should().NotBeEquivalentTo(original);
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static int Run(params string[] args)
    {
        lock (ConsoleLock)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            using var sink = new StringWriter();
            Console.SetOut(sink);
            Console.SetError(sink);
            try
            {
                return CliRunner.Run(args);
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
        }
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
