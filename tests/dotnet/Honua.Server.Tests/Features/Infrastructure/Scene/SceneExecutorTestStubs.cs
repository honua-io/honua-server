// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

internal sealed class StubLayerCatalog : ILayerCatalog
{
    public LayerDefinition? Layer { get; set; }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Layer);

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Layer is null ? Array.Empty<LayerDefinition>() : new[] { Layer });

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult<ServiceDefinition?>(null);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<ServiceDefinition>());

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Layer is not null);

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship?>(null);

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<Relationship>());
}

internal sealed class StubFeatureSource : ISceneFeatureSource
{
    public IReadOnlyList<SceneFeature> Features { get; set; } = Array.Empty<SceneFeature>();
    public int StreamInvocationCount { get; private set; }

    public async IAsyncEnumerable<SceneFeature> StreamAsync(
        LayerDefinition layer,
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
