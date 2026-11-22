// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Exports;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

// A published version is a self-contained file tree: every file it names is stored under it.
// The advisory Export check (.github/workflows/export-portolan-check.yml) sets
// HONUA_EXPORT_SNAPSHOT_DIR and runs the pinned Portolan validator over the copy this case writes.
public sealed partial class ExportEndpointTests
{
    private const string SnapshotDirectoryVariable = "HONUA_EXPORT_SNAPSHOT_DIR";

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task ExportVersion_CopiesAsSelfContainedFileTree()
    {
        var published = await PublishAsync(IdentityField);
        var descriptor = await ReadCollectionAsync(published.CollectionKey);
        var versionPrefix = ArtifactKey(published, string.Empty);
        var requested = Environment.GetEnvironmentVariable(SnapshotDirectoryVariable);
        var target = string.IsNullOrEmpty(requested) ? Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString()) : requested;
        try
        {
            var copied = await CopyTreeAsync(versionPrefix, target);

            copied.Should().Contain([ExportCollectionDocument.FileName,
                descriptor.Artifacts.Geoparquet.Href, descriptor.Artifacts.Pmtiles.Href]);
            (await File.ReadAllBytesAsync(Path.Join(target, descriptor.Artifacts.Geoparquet.Href))).Should().Equal(
                await ReadBytesAsync(ArtifactKey(published, descriptor.Artifacts.Geoparquet.Href)));
        }
        finally
        {
            if (string.IsNullOrEmpty(requested) && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    private async Task<IReadOnlyList<string>> CopyTreeAsync(string prefix, string target)
    {
        var copied = new List<string>();
        // FileId is the logical key; StoragePath may name a physical generation of it.
        var keys = (await Storage.ListFilesAsync(prefix)).Select(file => file.FileId)
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal));
        foreach (var key in keys)
        {
            var relative = key[prefix.Length..];
            var path = Path.Join(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, await ReadBytesAsync(key));
            copied.Add(relative);
        }

        return copied;
    }
}
