// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Server.Tests.Infrastructure;

internal sealed class InMemoryMetadataResourceStore : IMetadataResourceStore
{
    private const string DefaultNamespace = "default";
    private readonly object _sync = new();
    private readonly Dictionary<MetadataResourceIdentifier, StoredResource> _resources = new();
    private readonly ConcurrentDictionary<(string ResourceId, string ResourceVersion), CompiledMetadataArtifact> _artifacts = new();

    public Task<MetadataResource?> GetAsync(
        MetadataResourceIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        lock (_sync)
        {
            return Task.FromResult(_resources.TryGetValue(identifier, out var stored)
                ? CloneResource(stored.Resource)
                : null);
        }
    }

    public Task<IReadOnlyList<MetadataResource>> ListAsync(
        string? kind = null,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IEnumerable<MetadataResource> query = _resources.Values.Select(entry => entry.Resource);

            if (!string.IsNullOrWhiteSpace(kind))
            {
                query = query.Where(resource => string.Equals(resource.Kind, kind, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(@namespace))
            {
                query = query.Where(resource =>
                    string.Equals(resource.Metadata?.Namespace, @namespace, StringComparison.Ordinal));
            }

            var results = query
                .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
                .ThenBy(resource => resource.Metadata?.Namespace, StringComparer.Ordinal)
                .ThenBy(resource => resource.Metadata?.Name, StringComparer.Ordinal)
                .Select(CloneResource)
                .ToList();

            return Task.FromResult<IReadOnlyList<MetadataResource>>(results);
        }
    }

    public Task<MetadataResourceWriteResult> CreateAsync(
        MetadataResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var normalized = Normalize(resource, resource.Metadata);
        var identifier = BuildIdentifier(normalized);

        lock (_sync)
        {
            if (_resources.ContainsKey(identifier))
            {
                return Task.FromResult(
                    MetadataResourceWriteResult.Failure(MetadataResourceWriteOutcome.Conflict, "Resource already exists."));
            }

            var now = DateTimeOffset.UtcNow;
            var metadata = normalized.Metadata ?? new ResourceMetadata();
            var createdMetadata = metadata with
            {
                Id = string.IsNullOrWhiteSpace(metadata.Id) ? Guid.NewGuid().ToString("N") : metadata.Id,
                Namespace = string.IsNullOrWhiteSpace(metadata.Namespace) ? DefaultNamespace : metadata.Namespace,
                ResourceVersion = "1",
                Generation = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            var created = new MetadataResource
            {
                ApiVersion = normalized.ApiVersion,
                Kind = normalized.Kind,
                Metadata = createdMetadata,
                Spec = normalized.Spec,
                Status = normalized.Status
            };

            _resources[identifier] = new StoredResource(created, 1);

            return Task.FromResult(MetadataResourceWriteResult.Success(MetadataResourceWriteOutcome.Created, CloneResource(created)));
        }
    }

    public Task<MetadataResourceWriteResult> UpdateAsync(
        MetadataResource resource,
        long expectedResourceVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var normalized = Normalize(resource, resource.Metadata);
        var identifier = BuildIdentifier(normalized);

        lock (_sync)
        {
            if (!_resources.TryGetValue(identifier, out var stored))
            {
                return Task.FromResult(
                    MetadataResourceWriteResult.Failure(MetadataResourceWriteOutcome.Conflict, "Resource version conflict or resource not found."));
            }

            if (stored.Version != expectedResourceVersion)
            {
                return Task.FromResult(
                    MetadataResourceWriteResult.Failure(MetadataResourceWriteOutcome.Conflict, "Resource version conflict or resource not found."));
            }

            var existing = stored.Resource;
            var now = DateTimeOffset.UtcNow;
            var specChanged = !JsonEquals(existing.Spec, normalized.Spec);
            var nextVersion = stored.Version + 1;
            var nextGeneration = specChanged
                ? (existing.Metadata?.Generation ?? 1) + 1
                : (existing.Metadata?.Generation ?? 1);

            var metadata = normalized.Metadata ?? new ResourceMetadata();
            var updatedMetadata = metadata with
            {
                Id = existing.Metadata?.Id,
                Name = identifier.Name,
                Namespace = identifier.Namespace,
                ResourceVersion = nextVersion.ToString(CultureInfo.InvariantCulture),
                Generation = nextGeneration,
                CreatedAt = existing.Metadata?.CreatedAt ?? now,
                UpdatedAt = now
            };

            var updated = new MetadataResource
            {
                ApiVersion = normalized.ApiVersion,
                Kind = normalized.Kind,
                Metadata = updatedMetadata,
                Spec = normalized.Spec,
                Status = normalized.Status
            };

            _resources[identifier] = new StoredResource(updated, nextVersion);

            return Task.FromResult(MetadataResourceWriteResult.Success(MetadataResourceWriteOutcome.Updated, CloneResource(updated)));
        }
    }

    public Task<MetadataResourceWriteResult> DeleteAsync(
        MetadataResourceIdentifier identifier,
        long expectedResourceVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        lock (_sync)
        {
            if (!_resources.TryGetValue(identifier, out var stored) || stored.Version != expectedResourceVersion)
            {
                return Task.FromResult(
                    MetadataResourceWriteResult.Failure(MetadataResourceWriteOutcome.Conflict, "Resource version conflict or resource not found."));
            }

            _resources.Remove(identifier);
            return Task.FromResult(
                MetadataResourceWriteResult.Success(MetadataResourceWriteOutcome.Deleted, CloneResource(stored.Resource)));
        }
    }

    public Task StoreCompiledArtifactAsync(
        CompiledMetadataArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var key = (artifact.ResourceId ?? string.Empty, artifact.ResourceVersion ?? string.Empty);
        _artifacts[key] = artifact;
        return Task.CompletedTask;
    }

    public Task<CompiledMetadataArtifact?> GetCompiledArtifactAsync(
        string resourceId,
        string resourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("Resource ID is required.", nameof(resourceId));
        }

        var key = (resourceId, resourceVersion ?? string.Empty);
        return Task.FromResult(_artifacts.TryGetValue(key, out var artifact) ? artifact : null);
    }

    private static MetadataResource Normalize(MetadataResource resource, ResourceMetadata? metadata)
    {
        var normalizedMetadata = metadata ?? new ResourceMetadata();
        var @namespace = string.IsNullOrWhiteSpace(normalizedMetadata.Namespace)
            ? DefaultNamespace
            : normalizedMetadata.Namespace;
        var name = normalizedMetadata.Name;

        return new MetadataResource
        {
            ApiVersion = resource.ApiVersion,
            Kind = resource.Kind,
            Metadata = normalizedMetadata with
            {
                Namespace = @namespace,
                Name = name
            },
            Spec = resource.Spec,
            Status = resource.Status
        };
    }

    private static MetadataResourceIdentifier BuildIdentifier(MetadataResource resource)
    {
        var kind = resource.Kind ?? string.Empty;
        var metadata = resource.Metadata ?? new ResourceMetadata();
        var @namespace = string.IsNullOrWhiteSpace(metadata.Namespace) ? DefaultNamespace : metadata.Namespace!;
        var name = metadata.Name ?? string.Empty;
        return new MetadataResourceIdentifier(kind, @namespace, name);
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        var leftJson = JsonSerializer.Serialize(left);
        var rightJson = JsonSerializer.Serialize(right);
        return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
    }

    private static MetadataResource CloneResource(MetadataResource resource)
    {
        return new MetadataResource
        {
            ApiVersion = resource.ApiVersion,
            Kind = resource.Kind,
            Metadata = resource.Metadata,
            Spec = resource.Spec,
            Status = resource.Status
        };
    }

    private sealed record StoredResource(MetadataResource Resource, long Version);
}
