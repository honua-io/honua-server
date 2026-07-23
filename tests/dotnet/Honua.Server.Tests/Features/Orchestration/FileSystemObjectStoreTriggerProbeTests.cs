// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Orchestration;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class FileSystemObjectStoreTriggerProbeTests : IDisposable
{
    private readonly string _root;

    public FileSystemObjectStoreTriggerProbeTests()
    {
        // The second Path.Combine argument is a literal prefix plus a GUID (never rooted), so
        // GetTempPath() is never dropped.
        _root = Path.Join(Path.GetTempPath(), "honua-objstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private FileSystemObjectStoreTriggerProbe BuildProbe()
        => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["drop"] = _root });

    private string Write(string relativePath, DateTimeOffset modified)
    {
        // This private helper is only called from this file with relative literal paths
        // (e.g. "in/a.csv"), so relativePath is never rooted and _root is never dropped.
        var full = Path.Join(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        File.SetLastWriteTimeUtc(full, modified.UtcDateTime);
        return full;
    }

    [Fact]
    public async Task ProbeNewestAsync_EmptyStore_ReturnsNull()
    {
        var result = await BuildProbe().ProbeNewestAsync(new ObjectStoreTriggerConfig { StoreId = "drop" });
        Assert.Null(result);
    }

    [Fact]
    public async Task ProbeNewestAsync_UnregisteredStore_ReturnsNull()
    {
        var result = await BuildProbe().ProbeNewestAsync(new ObjectStoreTriggerConfig { StoreId = "missing" });
        Assert.Null(result);
    }

    [Fact]
    public async Task ProbeNewestAsync_ReturnsNewestObjectByMarker()
    {
        Write("in/a.csv", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Write("in/b.csv", new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));
        Write("in/c.csv", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));

        var result = await BuildProbe().ProbeNewestAsync(
            new ObjectStoreTriggerConfig { StoreId = "drop", Prefix = "in" });

        Assert.NotNull(result);
        if (result is null)
        {
            throw new InvalidOperationException("ProbeNewestAsync should have returned a result.");
        }

        Assert.Equal("in/b.csv", result.Value.Key);
    }

    [Fact]
    public async Task ProbeNewestAsync_NewObject_AdvancesMarker()
    {
        Write("in/a.csv", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var first = await BuildProbe().ProbeNewestAsync(
            new ObjectStoreTriggerConfig { StoreId = "drop", Prefix = "in" });
        Assert.NotNull(first);

        Write("in/z.csv", new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero));
        var second = await BuildProbe().ProbeNewestAsync(
            new ObjectStoreTriggerConfig { StoreId = "drop", Prefix = "in" });

        Assert.NotNull(second);
        if (first is null || second is null)
        {
            throw new InvalidOperationException("ProbeNewestAsync should have returned results for both probes.");
        }

        // The newest marker must strictly advance when a newer object lands.
        Assert.True(string.CompareOrdinal(second.Value.Marker, first.Value.Marker) > 0);
        Assert.Equal("in/z.csv", second.Value.Key);
    }

    [Fact]
    public async Task ProbeNewestAsync_PrefixEscapeAttempt_ReturnsNull()
    {
        var result = await BuildProbe().ProbeNewestAsync(
            new ObjectStoreTriggerConfig { StoreId = "drop", Prefix = "../../etc" });

        Assert.Null(result);
    }
}
