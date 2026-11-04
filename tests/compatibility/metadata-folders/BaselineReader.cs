// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class FolderSnapshotCompatibilityTests
{
    [ExternalServiceTest("HONUA_FOLDER_COMPAT_DIRECTORY")]
    public async Task BaselineReader_ReadsNewSnapshotAndProvesSaveStageFieldLoss()
    {
        var snapshot = await InstallSnapshot("new");
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph));
        serialized.RootElement.TryGetProperty("folders", out _).Should().BeFalse();
        serialized.RootElement.TryGetProperty("servicePathAliases", out _).Should().BeFalse();
        foreach (var service in serialized.RootElement.GetProperty("services").EnumerateArray())
        {
            service.TryGetProperty("folderId", out _).Should().BeFalse();
            service.TryGetProperty("publicName", out _).Should().BeFalse();
        }
        await RestartHost();
        await AssertFlatReads();
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var staged = await ((IMetadataV2GraphRevisionStager)store).StageAsync(snapshot.Graph);
        await WriteSnapshot("old-stage", staged);
        var saved = await store.SaveAsync(snapshot.Graph, snapshot.Etag);
        await WriteSnapshot("old-save", saved);
        (await store.GetCurrentAsync()).Etag.Should().Be(saved.Etag);
    }

}
