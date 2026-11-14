// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed class ImportScratchIsolationTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Theory]
    [InlineData("upload.zip")]
    [InlineData("upload.gpkg")]
    [InlineData("upload.kmz")]
    [InlineData("upload.gdb.zip")]
    [InlineData("upload.parquet")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Preview_ScratchDirectory_IsPrivateAndUniqueUnderTheTempPath(string fileName)
    {
        var service = PreviewImportServiceFactory.Create();
        await using var first = new ScratchProbeUpload();
        await using var second = new ScratchProbeUpload();

        await Task.WhenAll(
            service.Invoking(s => s.PreviewFileAsync(first, fileName)).Should().ThrowAsync<IOException>(),
            service.Invoking(s => s.PreviewFileAsync(second, fileName)).Should().ThrowAsync<IOException>());

        var tempPath = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        foreach (var probe in new[] { first, second })
        {
            probe.ScratchDirectory.Should().NotBeNull("the failure must occur after the scratch directory exists");
            Path.GetDirectoryName(probe.ScratchDirectory).Should().Be(tempPath,
                "a shared intermediate directory owned by another OS user would block every later import");
            if (!OperatingSystem.IsWindows())
            {
                probe.ScratchDirectoryMode.Should().Be(OwnerOnly);
            }

            Directory.Exists(probe.ScratchDirectory).Should().BeFalse();
        }

        first.ScratchDirectory.Should().NotBe(second.ScratchDirectory);
    }

    private sealed class ScratchProbeUpload : MemoryStream
    {
        public string? ScratchDirectory { get; private set; }
        public UnixFileMode? ScratchDirectoryMode { get; private set; }
        public override bool CanSeek => false;

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            ScratchDirectory = Path.GetDirectoryName(destination.Should().BeOfType<FileStream>().Subject.Name)!;
            if (!OperatingSystem.IsWindows())
            {
                ScratchDirectoryMode = File.GetUnixFileMode(ScratchDirectory);
            }

            throw new IOException("fixture upload interrupted");
        }
    }
}
