// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Security;

public sealed class PrivateTempFileTests
{
    [UnitTest]
    public void NewPath_WithPrefixAndExtension_ReturnsUniquePathDirectlyUnderTempDirectory()
    {
        var first = PrivateTempFile.NewPath("honua-test-", ".tif");
        var second = PrivateTempFile.NewPath("honua-test-", ".tif");

        Path.GetDirectoryName(first).Should().Be(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        Path.GetFileName(first).Should().StartWith("honua-test-").And.EndWith(".tif");
        first.Should().NotBe(second);
        Path.GetFileName(PrivateTempFile.NewPath("honua-test-")).Should().HaveLength("honua-test-".Length + 32);
    }

    [UnitTest]
    public void NewPath_WithInvalidPrefixOrExtension_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => PrivateTempFile.NewPath("../honua-")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PrivateTempFile.NewPath("honua\\x-")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PrivateTempFile.NewPath(" ")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PrivateTempFile.NewPath("honua-", "/etc")).Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Create_WithNewThenExistingPath_CreatesOwnerOnlyFileThenRefusesReuse()
    {
        var path = PrivateTempFile.NewPath("honua-private-temp-test-", ".bin");
        try
        {
            using (var stream = PrivateTempFile.Create(path))
            {
                stream.WriteByte(1);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(PrivateTempFile.OwnerReadWrite);
            }

            FluentActions.Invoking(() => PrivateTempFile.Create(path).Dispose()).Should().Throw<IOException>(
                "a pre-existing file or link at the path must never be reused");
            new FileInfo(path).Length.Should().Be(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    public void ApplyPrivateCreateNew_WithWritableOptions_SetsExclusiveModeAndOwnerOnlyPermissions()
    {
        var options = PrivateTempFile.ApplyPrivateCreateNew(new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write });

        options.Mode.Should().Be(FileMode.CreateNew);
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode.Should().Be(PrivateTempFile.OwnerReadWrite);
        }
    }

    [UnitTest]
    public void CreateDirectory_WithNewAndExistingDirectories_PreservesExistingModeAndReportsSharedWrite()
    {
        var root = PrivateTempFile.NewPath("honua-private-dir-test-");
        try
        {
            var nested = Path.Join(root, "nested");
            PrivateTempFile.CreateDirectory(nested).Exists.Should().BeTrue();
            PrivateTempFile.IsWritableByOthers(nested).Should().BeFalse();
            PrivateTempFile.IsWritableByOthers(Path.Join(root, "missing")).Should().BeFalse();
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.GetUnixFileMode(nested).Should().Be(PrivateTempFile.OwnerDirectory);

            File.SetUnixFileMode(nested, PrivateTempFile.OwnerDirectory | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            PrivateTempFile.IsWritableByOthers(nested).Should().BeTrue();
            PrivateTempFile.CreateDirectory(nested);
            File.GetUnixFileMode(nested).Should().HaveFlag(UnixFileMode.OtherWrite, "an existing directory keeps its mode");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [UnitTest]
    public void CreateOrClaimDirectory_WithOwnedSharedDirectory_RemovesSharedWrite()
    {
        var root = PrivateTempFile.NewPath("honua-private-claim-test-");
        try
        {
            PrivateTempFile.CreateOrClaimDirectory(root).Exists.Should().BeTrue();
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.GetUnixFileMode(root).Should().Be(PrivateTempFile.OwnerDirectory);
            var shared = PrivateTempFile.OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(root, shared);

            PrivateTempFile.CreateOrClaimDirectory(root);

            PrivateTempFile.IsWritableByOthers(root).Should().BeFalse();
            File.GetUnixFileMode(root).Should().Be(
                PrivateTempFile.OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [UnitTest]
    public void CreateOrClaimDirectory_WithDirectoryOwnedByAnotherUser_ThrowsInvalidOperationException()
    {
        // The root directory is owned by root; only a non-root process can prove the refusal.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        FluentActions.Invoking(() => PrivateTempFile.CreateOrClaimDirectory("/"))
            .Should().Throw<InvalidOperationException>().WithMessage("*owned by another user*");
    }

    [UnitTest]
    public void Create_WithDanglingSymlink_ThrowsWithoutCreatingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = PrivateTempFile.NewPath("honua-private-link-target-");
        var link = PrivateTempFile.NewPath("honua-private-link-");
        File.CreateSymbolicLink(link, target);
        try
        {
            FluentActions.Invoking(() => PrivateTempFile.Create(link).Dispose()).Should().Throw<IOException>();
            File.Exists(target).Should().BeFalse("exclusive creation must not create the link target");
        }
        finally
        {
            File.Delete(link);
            File.Delete(target);
        }
    }
}
