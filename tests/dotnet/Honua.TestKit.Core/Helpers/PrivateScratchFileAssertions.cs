// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.TestKit.Helpers;

public static class PrivateScratchFileAssertions
{
    public static void AssertOwnerOnly(Stream stream)
    {
        var file = stream.Should().BeOfType<FileStream>().Which;
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(file.SafeFileHandle).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // For callbacks whose exceptions the code under test would turn into an ordinary failure:
    // record the violation there, then assert it is null after the call returns.
    public static string? OwnerOnlyViolation(Stream stream)
    {
        if (stream is not FileStream file)
        {
            return $"expected a file-backed scratch stream, got {stream.GetType().Name}";
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var mode = File.GetUnixFileMode(file.SafeFileHandle);
        return mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite) ? null : $"scratch file mode was {mode}";
    }

    // Taken before the operation, so files other runs left in the shared temp path are ignored.
    public static IReadOnlySet<string> Snapshot(string prefix)
        => new HashSet<string>(Directory.GetFiles(Path.GetTempPath(), prefix + "*"), StringComparer.Ordinal);

    public static void AssertOwnerOnly(string prefix, IReadOnlySet<string> before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var files = Directory.GetFiles(Path.GetTempPath(), prefix + "*").Where(path => !before.Contains(path)).ToList();
        files.Should().NotBeEmpty("the operation must have created its scratch file");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var observedFiles = 0;
        foreach (var path in files)
        {
            if (TryGetMode(path) is { } mode)
            {
                mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
                observedFiles++;
            }
        }

        observedFiles.Should().BePositive("the operation keeps its scratch file open during this assertion");
    }

    // A parallel operation may have disposed its own scratch file in the meantime.
    private static UnixFileMode? TryGetMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.GetUnixFileMode(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
