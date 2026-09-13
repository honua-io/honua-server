// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Db.Postgres.Features.FileImport;
using Microsoft.Extensions.Logging;
using Honua.TestKit.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed class ImportScratchCleanupTests
{
    [Theory]
    [InlineData("upload.zip", false)]
    [InlineData("upload.gpkg", false)]
    [InlineData("upload.kmz", false)]
    [InlineData("upload.gdb.zip", false)]
    [InlineData("upload.parquet", false)]
    [InlineData("upload.zip", true)]
    [InlineData("upload.gpkg", true)]
    [InlineData("upload.kmz", true)]
    [InlineData("upload.gdb.zip", true)]
    [InlineData("upload.parquet", true)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Preview_CopyFailsAfterScratchFileCreated_RemovesOwnedDirectory(string fileName, bool cancel)
    {
        var service = PreviewImportServiceFactory.Create();
        using var cancellation = new CancellationTokenSource();
        await using var source = new InterruptedUpload(cancellation, cancel);

        var action = () => service.PreviewFileAsync(source, fileName, cancellation.Token);
        if (cancel)
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await action.Should().ThrowAsync<IOException>().WithMessage("fixture upload interrupted");
        }

        source.ScratchFile.Should().NotBeNull("failure must occur after a real scratch file was created");
        File.Exists(source.ScratchFile).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(source.ScratchFile!)).Should().BeFalse(
            "the failed attempt must remove its directory and every partial file");
    }

    [UnitTest]
    public async Task Preview_ValidKmz_ReadsFixtureAndRemovesExtractedFiles()
    {
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open(), Encoding.UTF8))
        {
            writer.Write("""
                <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark>
                <name>Harbor</name><Point><coordinates>-157.5,21.25,12.5</coordinates></Point>
                </Placemark></Document></kml>
                """);
        }
        await using var source = new ObservedUpload(archiveBytes.ToArray());
        var preview = await PreviewImportServiceFactory.Create().PreviewFileAsync(source, "harbor.kmz");
        preview.TotalFeatureCount.Should().Be(1);
        preview.DetectedSrid.Should().Be(4326);
        preview.SampleProperties["name"].Should().Be("Harbor");
        source.ScratchFile.Should().NotBeNull();
        Directory.Exists(Path.GetDirectoryName(source.ScratchFile!)).Should().BeFalse();
    }

    [UnitTest]
    public async Task Preview_CleanupDenied_LogsWarningAndPreservesOriginalFailure()
    {
        var logger = new CleanupLogger();
        var service = new StreamingFileImportService(new ThrowingConnectionProvider(),
            new NoopCrsDetectionService(), new TestFileFormatDetectionService(),
            new NoopPerformanceMonitor(), logger);
        await using var source = new UndeletableUpload();
        try
        {
            var action = () => service.PreviewFileAsync(source, "blocked.gpkg");
            await action.Should().ThrowAsync<IOException>().WithMessage("original upload failure");
            source.ScratchDirectory.Should().NotBeNull();
            Directory.Exists(source.ScratchDirectory).Should().BeTrue("the fixture must actually deny cleanup");
            logger.Warnings.Should().ContainSingle();
            logger.Warnings[0].Should().Contain(source.ScratchDirectory!);
        }
        finally
        {
            source.ReleaseDirectory();
        }
    }

    private sealed class UndeletableUpload : MemoryStream
    {
        private FileStream? _lockedFile;
        public string? ScratchDirectory { get; private set; }
        public override bool CanSeek => false;
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            ScratchDirectory = Path.GetDirectoryName(((FileStream)destination).Name)!;
            await destination.WriteAsync(new byte[] { 1, 2, 3, 4 }, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            if (OperatingSystem.IsWindows())
            {
                _lockedFile = new FileStream(Path.Join(ScratchDirectory, "locked"), FileMode.Create,
                    FileAccess.ReadWrite, FileShare.None);
            }
            else
            {
                File.SetUnixFileMode(ScratchDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            throw new IOException("original upload failure");
        }

        public void ReleaseDirectory()
        {
            _lockedFile?.Dispose();
            if (ScratchDirectory is not null)
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(ScratchDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                Directory.Delete(ScratchDirectory, recursive: true);
            }
        }
    }

    private sealed class CleanupLogger : ILogger<StreamingFileImportService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class ObservedUpload(byte[] bytes) : MemoryStream(bytes)
    {
        public string? ScratchFile { get; private set; }
        public override bool CanSeek => false;
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            ScratchFile = destination.Should().BeOfType<FileStream>().Subject.Name;
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }
    }

    private sealed class InterruptedUpload(CancellationTokenSource cancellation, bool cancel) : MemoryStream
    {
        public string? ScratchFile { get; private set; }
        public override bool CanSeek => false;

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            var file = destination.Should().BeOfType<FileStream>().Subject;
            ScratchFile = file.Name;
            await file.WriteAsync(new byte[] { 1, 2, 3, 4 }, cancellationToken);
            await file.FlushAsync(cancellationToken);
            new FileInfo(file.Name).Length.Should().Be(4);
            if (cancel)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new IOException("fixture upload interrupted");
        }
    }
}
