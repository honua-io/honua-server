// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

internal sealed class StubFeatureSource : ISceneFeatureSource
{
    public IReadOnlyList<SceneFeature> Features { get; set; } = Array.Empty<SceneFeature>();
    public int StreamInvocationCount { get; private set; }

    public async IAsyncEnumerable<SceneFeature> StreamAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        IReadOnlyList<string> includeAttributes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamInvocationCount++;
        foreach (var feature in Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
            await Task.Yield();
        }
    }
}

internal sealed class StubRegistrationService : ISceneRegistrationService
{
    public List<SceneDatasetRecord> Records { get; } = new();
    public List<Guid> DeactivatedDatasetIds { get; } = new();
    public bool RejectNextRegistration { get; set; }

    public Task<SceneDatasetRecord> RegisterAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
    {
        if (RejectNextRegistration)
        {
            throw new SceneDatasetAlreadyExistsException("duplicate");
        }
        Records.Add(record);
        return Task.FromResult(record);
    }

    public Task<SceneDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => Task.FromResult(Records.Find(r => r.DatasetId == datasetId));

    public Task<SceneDatasetRecord?> GetBySceneIdAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(Records.Find(r => r.Id == id));

    public Task<IReadOnlyList<SceneDatasetRecord>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SceneDatasetRecord>>(Records);

    public Task<SceneDatasetRecord> UpdateAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
        => Task.FromResult(record);

    public Task<bool> DeactivateAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        DeactivatedDatasetIds.Add(datasetId);
        var idx = Records.FindIndex(r => r.DatasetId == datasetId);
        if (idx < 0)
        {
            return Task.FromResult(false);
        }
        Records[idx] = Records[idx] with { Status = SceneDatasetStatus.Inactive };
        return Task.FromResult(true);
    }
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Honua.Tests";
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
