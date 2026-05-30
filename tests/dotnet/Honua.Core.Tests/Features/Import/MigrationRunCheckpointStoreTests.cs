// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Behaviour tests for <see cref="InMemoryMigrationRunCheckpointStore"/> and
/// <see cref="FileSystemMigrationRunCheckpointStore"/> (issue #1033 slice 3).
/// </summary>
public sealed class MigrationRunCheckpointStoreTests
{
    [Fact]
    public async Task InMemory_SaveAndLoad_RoundTripsCheckpoint()
    {
        var store = new InMemoryMigrationRunCheckpointStore();
        var checkpoint = NewCheckpoint("run-1", "apply", "ws:1/layer:42", completed: 42);

        await store.SaveAsync(checkpoint);
        var loaded = await store.LoadAsync("run-1");

        loaded.Should().NotBeNull();
        loaded!.Phase.Should().Be("apply");
        loaded.ResumeMarker.Should().Be("ws:1/layer:42");
        loaded.CompletedItemCount.Should().Be(42);
        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task InMemory_SaveOverwrites_PreviousCheckpoint()
    {
        var store = new InMemoryMigrationRunCheckpointStore();
        await store.SaveAsync(NewCheckpoint("run-1", "scan", "layer-100", completed: 100, attempt: 1));
        await store.SaveAsync(NewCheckpoint("run-1", "apply", "layer-150", completed: 150, attempt: 2));

        var loaded = await store.LoadAsync("run-1");
        loaded.Should().NotBeNull();
        loaded!.Phase.Should().Be("apply");
        loaded.CompletedItemCount.Should().Be(150);
        loaded.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task InMemory_Delete_RemovesCheckpoint()
    {
        var store = new InMemoryMigrationRunCheckpointStore();
        await store.SaveAsync(NewCheckpoint("run-2", "apply", "marker", completed: 5));
        (await store.DeleteAsync("run-2")).Should().BeTrue();
        (await store.LoadAsync("run-2")).Should().BeNull();
        (await store.DeleteAsync("run-2")).Should().BeFalse();
    }

    [Fact]
    public async Task InMemory_Load_ForUnknownRun_ReturnsNull()
    {
        var store = new InMemoryMigrationRunCheckpointStore();
        (await store.LoadAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_RedactsUrlLikeMarker_AndCapsLength()
    {
        var store = new InMemoryMigrationRunCheckpointStore();
        var leaky = "https://internal.example/secret-resume-token?key=abc";
        var longMarker = new string('a', MigrationRunCheckpointSanitizer.MaxMarkerLength * 2);

        await store.SaveAsync(NewCheckpoint("run-leaky", "apply", leaky, completed: 1));
        await store.SaveAsync(NewCheckpoint("run-long", "apply", longMarker, completed: 1));

        var redacted = await store.LoadAsync("run-leaky");
        redacted!.ResumeMarker.Should().Be(MigrationRunCheckpointSanitizer.RedactedMarker);

        var truncated = await store.LoadAsync("run-long");
        truncated!.ResumeMarker.Length.Should().Be(MigrationRunCheckpointSanitizer.MaxMarkerLength);
    }

    [Fact]
    public async Task FileSystem_RoundTrips_CheckpointAcrossInstances()
    {
        using var temp = new TempDirectory();
        using var first = new FileSystemMigrationRunCheckpointStore(temp.Path);
        await first.SaveAsync(NewCheckpoint("run-fs", "apply", "layer-77", completed: 77, attempt: 2));

        using var second = new FileSystemMigrationRunCheckpointStore(temp.Path);
        var loaded = await second.LoadAsync("run-fs");

        loaded.Should().NotBeNull();
        loaded!.Phase.Should().Be("apply");
        loaded.CompletedItemCount.Should().Be(77);
        loaded.Attempt.Should().Be(2);
        loaded.ResumeMarker.Should().Be("layer-77");

        (await second.DeleteAsync("run-fs")).Should().BeTrue();
        (await second.LoadAsync("run-fs")).Should().BeNull();
    }

    [Fact]
    public async Task FileSystem_RejectsPathTraversal_InRunId()
    {
        using var temp = new TempDirectory();
        using var store = new FileSystemMigrationRunCheckpointStore(temp.Path);
        // Run id containing path traversal must not escape the root directory.
        await store.SaveAsync(NewCheckpoint("../escape", "apply", "marker", completed: 1));

        Directory.GetFiles(temp.Path).Should().OnlyContain(path =>
            Path.GetDirectoryName(path) == temp.Path);
    }

    private static MigrationRunCheckpoint NewCheckpoint(
        string runId,
        string phase,
        string marker,
        int completed,
        int attempt = 1) => new()
        {
            RunId = runId,
            Phase = phase,
            ResumeMarker = marker,
            CompletedItemCount = completed,
            CapturedAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z", CultureInfo.InvariantCulture),
            Attempt = attempt
        };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"honua-mc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
