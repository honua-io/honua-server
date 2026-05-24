// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Metadata.Services;

/// <summary>
/// Default implementation of semantic metadata inventory, binding, and release package workflows.
/// </summary>
public sealed class MetadataReleaseService(
    IMetadataV2EnvironmentSnapshotReader snapshotReader,
    IMetadataReleasePackageStore packageStore,
    TimeProvider? timeProvider,
    ILogger<MetadataReleaseService> logger) : IMetadataReleaseService
{
    private const int MaxBindingEnvironments = 10;
    private const int MaxBindingSemanticIds = 500;
    private const int MaxPackageEntries = 1000;
    private const string ContentVersionAnnotation = "honua.io/content-version-id";
    private const string ContentVersionCamelAnnotation = "contentVersionId";
    private const string ProvenanceAnnotation = "honua.io/provenance-ref";
    private static readonly ActivitySource ActivitySource = new("Honua.Core.Metadata");

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<MetadataSemanticInventoryResponse?> GetSemanticInventoryAsync(
        string environment,
        MetadataSemanticInventoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ValidateEnvironment(environment);
        using var activity = ActivitySource.StartActivity("honua.metadata.release.inventory", ActivityKind.Internal);
        activity?.SetTag("metadata.environment", environment);

        var snapshot = await snapshotReader.GetCurrentAsync(environment, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var entries = ProjectInventory(snapshot, filter);
        activity?.SetTag("metadata.revision", snapshot.Revision);
        activity?.SetTag("metadata.inventory.count", entries.Count);
        MetadataReleaseServiceLog.InventoryProjected(logger, environment, snapshot.Revision, entries.Count);

        return new MetadataSemanticInventoryResponse
        {
            Environment = snapshot.Graph.Environment,
            Revision = snapshot.Revision,
            ETag = snapshot.Etag,
            GeneratedAt = _timeProvider.GetUtcNow(),
            Entries = entries,
        };
    }

    /// <inheritdoc />
    public async Task<MetadataEnvironmentBindingsResponse> GetEnvironmentBindingsAsync(
        MetadataEnvironmentBindingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environments = NormalizeRequiredList(request.Environments, nameof(request.Environments), MaxBindingEnvironments);
        var semanticIds = NormalizeRequiredList(request.SemanticIds, nameof(request.SemanticIds), MaxBindingSemanticIds);

        using var activity = ActivitySource.StartActivity("honua.metadata.release.bindings", ActivityKind.Internal);
        activity?.SetTag("metadata.environment.count", environments.Count);
        activity?.SetTag("metadata.semantic_id.count", semanticIds.Count);

        var bindings = new List<MetadataEnvironmentBindingSummary>(environments.Count * semanticIds.Count);
        foreach (var environment in environments)
        {
            var snapshot = await snapshotReader.GetCurrentAsync(environment, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                foreach (var semanticId in semanticIds)
                {
                    bindings.Add(new MetadataEnvironmentBindingSummary
                    {
                        SemanticId = semanticId,
                        Environment = environment,
                        State = MetadataEnvironmentBindingState.EnvironmentUnavailable,
                    });
                }
                continue;
            }

            foreach (var semanticId in semanticIds)
            {
                bindings.Add(ProjectBinding(snapshot, semanticId));
            }
        }

        MetadataReleaseServiceLog.BindingsResolved(logger, environments.Count, semanticIds.Count, bindings.Count);
        return new MetadataEnvironmentBindingsResponse
        {
            RequestedAt = _timeProvider.GetUtcNow(),
            Environments = environments,
            Bindings = bindings,
        };
    }

    /// <inheritdoc />
    public async Task<MetadataReleasePackage> CreateReleasePackageAsync(
        CreateMetadataReleasePackageRequest request,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEnvironment(request.SourceEnvironment);

        var targetEnvironments = NormalizeRequiredList(
            request.TargetEnvironments,
            nameof(request.TargetEnvironments),
            MaxBindingEnvironments);
        var semanticIds = NormalizeRequiredList(request.SemanticIds, nameof(request.SemanticIds), MaxPackageEntries);
        var provenance = NormalizeOptionalList(request.Provenance, nameof(request.Provenance));

        using var activity = ActivitySource.StartActivity("honua.metadata.release.package.create", ActivityKind.Internal);
        activity?.SetTag("metadata.source_environment", request.SourceEnvironment);
        activity?.SetTag("metadata.target_environment.count", targetEnvironments.Count);
        activity?.SetTag("metadata.semantic_id.count", semanticIds.Count);

        var sourceSnapshot = await snapshotReader.GetCurrentAsync(request.SourceEnvironment, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Metadata v2 environment '{request.SourceEnvironment}' does not have an active revision.");

        var targetSnapshots = new Dictionary<string, MetadataV2GraphSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetEnvironment in targetEnvironments)
        {
            var snapshot = await snapshotReader.GetCurrentAsync(targetEnvironment, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"Metadata v2 environment '{targetEnvironment}' does not have an active revision.");
            targetSnapshots[targetEnvironment] = snapshot;
        }

        var entries = new List<MetadataReleaseEntry>(semanticIds.Count);
        foreach (var semanticId in semanticIds)
        {
            var artifact = ResolveArtifact(sourceSnapshot, semanticId)
                ?? throw new KeyNotFoundException(
                    $"Semantic artifact '{semanticId}' was not found in source environment '{sourceSnapshot.Graph.Environment}'.");

            var targetStates = new List<MetadataReleaseTargetState>(targetEnvironments.Count);
            foreach (var targetEnvironment in targetEnvironments)
            {
                var binding = ProjectBinding(targetSnapshots[targetEnvironment], semanticId);
                targetStates.Add(new MetadataReleaseTargetState
                {
                    Environment = targetEnvironment,
                    CurrentMetadataRevision = binding.Revision,
                    CurrentContentVersionId = binding.ContentVersionId,
                    BindingState = binding.State,
                    BindingSummary = binding,
                });
            }

            var requestedContentVersionId = string.IsNullOrWhiteSpace(request.DesiredContentVersionId)
                ? null
                : request.DesiredContentVersionId!.Trim();
            var desiredContentVersionId = string.IsNullOrWhiteSpace(artifact.ContentVersionId)
                ? requestedContentVersionId
                : artifact.ContentVersionId;
            var desiredProvenance = provenance.Count > 0 ? provenance : artifact.Provenance;

            entries.Add(new MetadataReleaseEntry
            {
                SemanticId = semanticId,
                ArtifactKind = artifact.Kind,
                ResourceType = artifact.Resource?.Type ?? artifact.ParentResource?.Type,
                DesiredMetadataRevision = request.DesiredRevision ?? sourceSnapshot.Revision,
                DesiredContentVersionId = desiredContentVersionId,
                DesiredProvenance = desiredProvenance,
                ChangeClass = ResolveChangeClass(request, semanticId),
                TargetStates = targetStates,
                DependentSemanticIds = ResolveDependencies(artifact),
                Status = MetadataReleaseEntryStatus.Draft,
            });
        }

        var now = _timeProvider.GetUtcNow();
        var packageId = Guid.NewGuid();
        var packageKey = string.IsNullOrWhiteSpace(request.PackageKey)
            ? BuildPackageKey(request, now)
            : request.PackageKey!.Trim();
        var metadata = new MetadataV2ObjectMetadata
        {
            Id = packageId.ToString("D"),
            Name = packageKey,
            Namespace = string.IsNullOrWhiteSpace(request.Namespace) ? null : request.Namespace.Trim(),
            Title = string.IsNullOrWhiteSpace(request.Title) ? packageKey : request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Summary) ? null : request.Summary.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            Generation = 1,
            Labels = new Dictionary<string, string>
            {
                ["sourceEnvironment"] = sourceSnapshot.Graph.Environment,
                ["sourceRevision"] = sourceSnapshot.Revision.ToString(CultureInfo.InvariantCulture),
            },
            Annotations = new Dictionary<string, string>
            {
                ["metadata.honua.io/sourceEtag"] = sourceSnapshot.Etag,
            },
        };

        var package = new MetadataReleasePackage
        {
            PackageId = packageId,
            Metadata = metadata,
            SourceEnvironment = sourceSnapshot.Graph.Environment,
            SourceRevision = sourceSnapshot.Revision,
            SourceEtag = sourceSnapshot.Etag,
            TargetEnvironments = targetEnvironments,
            Entries = entries,
            Status = MetadataReleasePackageStatus.Draft,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "system" : createdBy.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var stored = await packageStore.CreateAsync(package, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("metadata.package_id", stored.PackageId);
        activity?.SetTag("metadata.source_revision", stored.SourceRevision);
        MetadataReleaseServiceLog.PackageCreated(
            logger,
            stored.PackageId,
            stored.SourceEnvironment,
            stored.SourceRevision,
            stored.TargetEnvironments.Count,
            stored.Entries.Count);
        return stored;
    }

    /// <inheritdoc />
    public Task<MetadataReleasePackage?> GetReleasePackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        if (packageId == Guid.Empty)
        {
            throw new ArgumentException("Package id is required.", nameof(packageId));
        }

        return packageStore.GetAsync(packageId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GitOpsMetadataReleaseManifest?> GetGitOpsManifestAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("honua.metadata.release.manifest.export", ActivityKind.Internal);
        activity?.SetTag("metadata.package_id", packageId);

        var package = await GetReleasePackageAsync(packageId, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return null;
        }

        var manifest = new GitOpsMetadataReleaseManifest
        {
            Metadata = package.Metadata,
            Spec = new GitOpsMetadataReleaseSpec
            {
                PackageId = package.PackageId,
                Source = new GitOpsMetadataReleaseSource
                {
                    Environment = package.SourceEnvironment,
                    Revision = package.SourceRevision,
                    ETag = package.SourceEtag,
                },
                Targets = package.TargetEnvironments
                    .Select(static environment => new GitOpsMetadataReleaseTarget { Environment = environment })
                    .ToArray(),
                Entries = package.Entries.Select(static entry => new GitOpsMetadataReleaseEntry
                {
                    SemanticId = entry.SemanticId,
                    ArtifactKind = entry.ArtifactKind,
                    ResourceType = entry.ResourceType,
                    DesiredMetadataRevision = entry.DesiredMetadataRevision,
                    DesiredContentVersionId = entry.DesiredContentVersionId,
                    DesiredProvenance = entry.DesiredProvenance,
                    ChangeClass = entry.ChangeClass,
                    TargetStates = entry.TargetStates,
                    DependentSemanticIds = entry.DependentSemanticIds,
                }).ToArray(),
            },
        };

        MetadataReleaseServiceLog.ManifestExported(logger, package.PackageId, package.Entries.Count);
        return manifest;
    }

    private static List<MetadataSemanticInventoryEntry> ProjectInventory(
        MetadataV2GraphSnapshot snapshot,
        MetadataSemanticInventoryFilter filter)
    {
        var entries = new List<MetadataSemanticInventoryEntry>(
            snapshot.Graph.Resources.Count
            + snapshot.Graph.Services.Count
            + snapshot.Graph.Publications.Count
            + snapshot.Graph.Catalogs.Count
            + snapshot.Graph.Policies.Count
            + snapshot.Graph.Roles.Count);

        foreach (var resource in snapshot.Graph.Resources)
        {
            AddIfMatches(entries, ToInventoryEntry(resource), filter);
            foreach (var field in resource.SchemaFields)
            {
                AddIfMatches(entries, ToInventoryEntry(resource, field), filter);
            }
        }

        foreach (var service in snapshot.Graph.Services)
        {
            AddIfMatches(entries, ToInventoryEntry(service), filter);
        }

        foreach (var publication in snapshot.Graph.Publications)
        {
            snapshot.Index.ResourcesById.TryGetValue(publication.ResourceId, out var resource);
            AddIfMatches(entries, ToInventoryEntry(publication, resource), filter);
        }

        foreach (var catalog in snapshot.Graph.Catalogs)
        {
            AddIfMatches(entries, ToInventoryEntry(catalog), filter);
        }

        foreach (var policy in snapshot.Graph.Policies)
        {
            AddIfMatches(entries, ToInventoryEntry(policy), filter);
        }

        foreach (var role in snapshot.Graph.Roles)
        {
            AddIfMatches(entries, ToInventoryEntry(role), filter);
        }

        return entries;
    }

    private static void AddIfMatches(
        List<MetadataSemanticInventoryEntry> entries,
        MetadataSemanticInventoryEntry entry,
        MetadataSemanticInventoryFilter filter)
    {
        if (filter.ArtifactKind is { } artifactKind && entry.ArtifactKind != artifactKind)
        {
            return;
        }

        if (filter.ResourceType is { } resourceType && entry.ResourceType != resourceType)
        {
            return;
        }

        entries.Add(entry);
    }

    private static MetadataEnvironmentBindingSummary ProjectBinding(
        MetadataV2GraphSnapshot snapshot,
        string semanticId)
    {
        var artifact = ResolveArtifact(snapshot, semanticId);
        if (artifact is null)
        {
            return new MetadataEnvironmentBindingSummary
            {
                SemanticId = semanticId,
                Environment = snapshot.Graph.Environment,
                State = MetadataEnvironmentBindingState.Missing,
                Revision = snapshot.Revision,
                ETag = snapshot.Etag,
                LastObservedAt = snapshot.LoadedAt,
            };
        }

        var storage = ResolveStorage(snapshot, artifact);
        var connection = ResolveConnection(snapshot, storage);
        return new MetadataEnvironmentBindingSummary
        {
            SemanticId = semanticId,
            Environment = snapshot.Graph.Environment,
            State = MetadataEnvironmentBindingState.Bound,
            Revision = snapshot.Revision,
            ETag = snapshot.Etag,
            ArtifactKind = artifact.Kind,
            Resource = artifact.Resource is not null ? ToResourceSummary(artifact.Resource) :
                artifact.ParentResource is not null ? ToResourceSummary(artifact.ParentResource) : null,
            Field = artifact.Field is not null && artifact.ParentResource is not null
                ? ToFieldSummary(artifact.ParentResource, artifact.Field)
                : null,
            Service = artifact.Service is not null ? ToServiceSummary(artifact.Service) : null,
            Publication = artifact.Publication is not null ? ToPublicationSummary(artifact.Publication) : null,
            Storage = storage is not null ? ToStorageSummary(storage) : null,
            Connection = connection is not null ? ToConnectionSummary(connection) : null,
            ContentVersionId = artifact.ContentVersionId,
            LastObservedAt = snapshot.LoadedAt,
        };
    }

    private static ResolvedSemanticArtifact? ResolveArtifact(MetadataV2GraphSnapshot snapshot, string semanticId)
    {
        if (snapshot.Index.ResourcesById.TryGetValue(semanticId, out var resource))
        {
            return new ResolvedSemanticArtifact(
                semanticId,
                MetadataSemanticArtifactKind.Resource,
                Resource: resource,
                ContentVersionId: ExtractContentVersion(resource.Metadata),
                ProvenanceRefs: ExtractProvenance(resource.Metadata));
        }

        if (snapshot.Index.ServicesById.TryGetValue(semanticId, out var service))
        {
            return new ResolvedSemanticArtifact(
                semanticId,
                MetadataSemanticArtifactKind.Service,
                Service: service,
                ContentVersionId: ExtractContentVersion(service.Metadata),
                ProvenanceRefs: ExtractProvenance(service.Metadata));
        }

        if (snapshot.Index.PublicationsById.TryGetValue(semanticId, out var publication))
        {
            snapshot.Index.ResourcesById.TryGetValue(publication.ResourceId, out var publicationResource);
            snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var publicationService);
            return new ResolvedSemanticArtifact(
                semanticId,
                MetadataSemanticArtifactKind.Publication,
                ParentResource: publicationResource,
                Service: publicationService,
                Publication: publication,
                ContentVersionId: ExtractContentVersion(publication.Metadata),
                ProvenanceRefs: ExtractProvenance(publication.Metadata));
        }

        if (snapshot.Index.CatalogsById.TryGetValue(semanticId, out var catalog))
        {
            return new ResolvedSemanticArtifact(
                semanticId,
                MetadataSemanticArtifactKind.Catalog,
                Catalog: catalog,
                ContentVersionId: ExtractContentVersion(catalog.Metadata),
                ProvenanceRefs: ExtractProvenance(catalog.Metadata));
        }

        if (snapshot.Index.PoliciesById.TryGetValue(semanticId, out var policy))
        {
            return new ResolvedSemanticArtifact(
                semanticId,
                MetadataSemanticArtifactKind.Policy,
                Policy: policy,
                ContentVersionId: ExtractContentVersion(policy.Metadata),
                ProvenanceRefs: ExtractProvenance(policy.Metadata));
        }

        if (snapshot.Index.RolesById.TryGetValue(semanticId, out var role))
        {
            return new ResolvedSemanticArtifact(
                semanticId,
                MetadataSemanticArtifactKind.Role,
                Role: role,
                ContentVersionId: ExtractContentVersion(role.Metadata),
                ProvenanceRefs: ExtractProvenance(role.Metadata));
        }

        foreach (var candidateResource in snapshot.Graph.Resources)
        {
            foreach (var field in candidateResource.SchemaFields)
            {
                var fieldSemanticId = GetFieldSemanticId(candidateResource, field);
                if (!string.Equals(fieldSemanticId, semanticId, StringComparison.Ordinal))
                {
                    continue;
                }

                return new ResolvedSemanticArtifact(
                    semanticId,
                    MetadataSemanticArtifactKind.Field,
                    ParentResource: candidateResource,
                    Field: field,
                    ContentVersionId: ExtractContentVersion(candidateResource.Metadata),
                    ProvenanceRefs: ExtractProvenance(candidateResource.Metadata));
            }
        }

        return null;
    }

    private static MetadataV2StorageBinding? ResolveStorage(
        MetadataV2GraphSnapshot snapshot,
        ResolvedSemanticArtifact artifact)
    {
        var resource = artifact.Resource ?? artifact.ParentResource;
        if (artifact.Publication?.StorageBindingId is { Length: > 0 } publicationStorageBindingId &&
            snapshot.Index.StorageBindingsById.TryGetValue(publicationStorageBindingId, out var publicationStorage))
        {
            return publicationStorage;
        }

        if (resource?.PrimaryStorageBindingId is { Length: > 0 } primaryStorageBindingId &&
            snapshot.Index.StorageBindingsById.TryGetValue(primaryStorageBindingId, out var primaryStorage))
        {
            return primaryStorage;
        }

        if (resource is null)
        {
            return null;
        }

        return snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].FirstOrDefault();
    }

    private static MetadataV2Connection? ResolveConnection(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2StorageBinding? storage)
    {
        if (storage?.ConnectionId is not { Length: > 0 } connectionId)
        {
            return null;
        }

        return snapshot.Index.ConnectionsById.TryGetValue(connectionId, out var connection) ? connection : null;
    }

    private static MetadataReleaseChangeClass ResolveChangeClass(
        CreateMetadataReleasePackageRequest request,
        string semanticId)
    {
        if (request.ChangeClasses is not null &&
            request.ChangeClasses.TryGetValue(semanticId, out var changeClass))
        {
            return changeClass;
        }

        return MetadataReleaseChangeClass.Metadata;
    }

    private static string[] ResolveDependencies(ResolvedSemanticArtifact artifact)
    {
        var dependencies = new List<string>(3);
        if (artifact.ParentResource is not null)
        {
            dependencies.Add(artifact.ParentResource.Metadata.Id);
        }

        if (artifact.Publication is not null)
        {
            dependencies.Add(artifact.Publication.ResourceId);
            dependencies.Add(artifact.Publication.ServiceId);
            if (!string.IsNullOrWhiteSpace(artifact.Publication.StorageBindingId))
            {
                dependencies.Add(artifact.Publication.StorageBindingId);
            }
        }

        return dependencies.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static MetadataSemanticInventoryEntry ToInventoryEntry(MetadataV2Resource resource)
        => new()
        {
            SemanticId = resource.Metadata.Id,
            ArtifactKind = MetadataSemanticArtifactKind.Resource,
            ResourceType = resource.Type,
            Name = resource.Metadata.Name,
            Namespace = resource.Metadata.Namespace,
            Title = resource.Metadata.Title,
            MetadataGeneration = resource.Metadata.Generation,
            Status = resource.Status,
            ContentVersionId = ExtractContentVersion(resource.Metadata),
            Provenance = ExtractProvenance(resource.Metadata),
            PolicyIds = resource.PolicyIds,
        };

    private static MetadataSemanticInventoryEntry ToInventoryEntry(MetadataV2Resource resource, MetadataV2Field field)
        => new()
        {
            SemanticId = GetFieldSemanticId(resource, field),
            ArtifactKind = MetadataSemanticArtifactKind.Field,
            ResourceType = resource.Type,
            ParentSemanticId = resource.Metadata.Id,
            Name = field.Name,
            Namespace = resource.Metadata.Namespace,
            Title = field.Title,
            MetadataGeneration = resource.Metadata.Generation,
            ContentVersionId = ExtractContentVersion(resource.Metadata),
            Provenance = ExtractProvenance(resource.Metadata),
            PolicyIds = resource.PolicyIds,
        };

    private static MetadataSemanticInventoryEntry ToInventoryEntry(MetadataV2Service service)
        => new()
        {
            SemanticId = service.Metadata.Id,
            ArtifactKind = MetadataSemanticArtifactKind.Service,
            ServiceType = service.ServiceType,
            Name = service.Metadata.Name,
            Namespace = service.Metadata.Namespace,
            Title = service.Metadata.Title,
            MetadataGeneration = service.Metadata.Generation,
            Status = service.Status,
            ContentVersionId = ExtractContentVersion(service.Metadata),
            Provenance = ExtractProvenance(service.Metadata),
        };

    private static MetadataSemanticInventoryEntry ToInventoryEntry(
        MetadataV2Publication publication,
        MetadataV2Resource? resource)
        => new()
        {
            SemanticId = publication.Metadata.Id,
            ArtifactKind = MetadataSemanticArtifactKind.Publication,
            ResourceType = resource?.Type,
            PublicationType = publication.PublicationType,
            ParentSemanticId = publication.ResourceId,
            Name = publication.Metadata.Name,
            Namespace = publication.Metadata.Namespace,
            Title = publication.TitleOverride ?? publication.Metadata.Title,
            MetadataGeneration = publication.Metadata.Generation,
            Status = publication.Status,
            ContentVersionId = ExtractContentVersion(publication.Metadata),
            Provenance = ExtractProvenance(publication.Metadata),
        };

    private static MetadataSemanticInventoryEntry ToInventoryEntry(MetadataV2Catalog catalog)
        => new()
        {
            SemanticId = catalog.Metadata.Id,
            ArtifactKind = MetadataSemanticArtifactKind.Catalog,
            Name = catalog.Metadata.Name,
            Namespace = catalog.Metadata.Namespace,
            Title = catalog.Metadata.Title,
            MetadataGeneration = catalog.Metadata.Generation,
            Status = catalog.Status,
            ContentVersionId = ExtractContentVersion(catalog.Metadata),
            Provenance = ExtractProvenance(catalog.Metadata),
        };

    private static MetadataSemanticInventoryEntry ToInventoryEntry(MetadataV2Policy policy)
        => new()
        {
            SemanticId = policy.Metadata.Id,
            ArtifactKind = MetadataSemanticArtifactKind.Policy,
            Name = policy.Metadata.Name,
            Namespace = policy.Metadata.Namespace,
            Title = policy.Metadata.Title,
            MetadataGeneration = policy.Metadata.Generation,
            Status = policy.Status,
            ContentVersionId = ExtractContentVersion(policy.Metadata),
            Provenance = ExtractProvenance(policy.Metadata),
        };

    private static MetadataSemanticInventoryEntry ToInventoryEntry(MetadataV2Role role)
        => new()
        {
            SemanticId = role.Metadata.Id,
            ArtifactKind = MetadataSemanticArtifactKind.Role,
            Name = role.Metadata.Name,
            Namespace = role.Metadata.Namespace,
            Title = role.Metadata.Title,
            MetadataGeneration = role.Metadata.Generation,
            Status = role.Status,
            ContentVersionId = ExtractContentVersion(role.Metadata),
            Provenance = ExtractProvenance(role.Metadata),
            PolicyIds = role.PolicyIds,
        };

    private static MetadataBoundResourceSummary ToResourceSummary(MetadataV2Resource resource)
        => new()
        {
            Id = resource.Metadata.Id,
            Name = resource.Metadata.Name,
            Namespace = resource.Metadata.Namespace,
            Title = resource.Metadata.Title,
            ResourceType = resource.Type,
            PrimaryStorageBindingId = resource.PrimaryStorageBindingId,
        };

    private static MetadataBoundFieldSummary ToFieldSummary(MetadataV2Resource resource, MetadataV2Field field)
        => new()
        {
            SemanticId = GetFieldSemanticId(resource, field),
            ParentResourceId = resource.Metadata.Id,
            FieldName = field.Name,
            FieldType = field.Type,
        };

    private static MetadataBoundServiceSummary ToServiceSummary(MetadataV2Service service)
        => new()
        {
            Id = service.Metadata.Id,
            Name = service.Metadata.Name,
            Namespace = service.Metadata.Namespace,
            Title = service.Metadata.Title,
            ServiceType = service.ServiceType,
            Route = service.Route,
        };

    private static MetadataBoundPublicationSummary ToPublicationSummary(MetadataV2Publication publication)
        => new()
        {
            Id = publication.Metadata.Id,
            ResourceId = publication.ResourceId,
            ServiceId = publication.ServiceId,
            PublicationType = publication.PublicationType,
            Path = publication.Path,
            LayerIndex = publication.LayerIndex,
            ServiceLocalId = publication.ServiceLocalId,
        };

    private static MetadataBoundStorageSummary ToStorageSummary(MetadataV2StorageBinding storage)
        => new()
        {
            Id = storage.Metadata.Id,
            ResourceId = storage.ResourceId,
            StorageType = storage.StorageType,
            Locator = storage.Locator,
            ConnectionId = storage.ConnectionId,
        };

    private static MetadataBoundConnectionSummary ToConnectionSummary(MetadataV2Connection connection)
        => new()
        {
            Id = connection.Metadata.Id,
            Name = connection.Metadata.Name,
            ConnectionType = connection.Type,
            Provider = connection.Provider,
            Endpoint = connection.Endpoint,
            SecretRef = connection.SecretRef,
        };

    private static string? ExtractContentVersion(MetadataV2ObjectMetadata metadata)
    {
        if (TryGetString(metadata.Annotations, ContentVersionAnnotation, out var annotationValue) ||
            TryGetString(metadata.Annotations, ContentVersionCamelAnnotation, out annotationValue) ||
            TryGetString(metadata.Labels, ContentVersionAnnotation, out annotationValue) ||
            TryGetString(metadata.Labels, ContentVersionCamelAnnotation, out annotationValue))
        {
            return annotationValue;
        }

        return null;
    }

    private static ConsoleProvenanceRef[] ExtractProvenance(MetadataV2ObjectMetadata metadata)
    {
        if (!TryGetString(metadata.Annotations, ProvenanceAnnotation, out var value))
        {
            return Array.Empty<ConsoleProvenanceRef>();
        }

        return
        [
            new ConsoleProvenanceRef
            {
                Kind = "metadata-v2",
                ItemId = value,
                Rel = "derived-from",
                Namespace = metadata.Namespace,
            },
        ];
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        if (values.TryGetValue(key, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string GetFieldSemanticId(MetadataV2Resource resource, MetadataV2Field field)
        => string.IsNullOrWhiteSpace(field.SemanticId)
            ? $"field.{resource.Metadata.Id}.{field.Name}"
            : field.SemanticId.Trim();

    private static List<string> NormalizeRequiredList(
        IReadOnlyList<string>? values,
        string parameterName,
        int maxCount)
    {
        if (values is null)
        {
            throw new ArgumentException($"{parameterName} must be an array.", parameterName);
        }

        if (values.Count == 0)
        {
            throw new ArgumentException($"{parameterName} must contain at least one value.", parameterName);
        }

        if (values.Count > maxCount)
        {
            throw new ArgumentException($"{parameterName} cannot contain more than {maxCount} values.", parameterName);
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{parameterName} cannot contain blank values.", parameterName);
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    private static IReadOnlyList<T> NormalizeOptionalList<T>(
        IReadOnlyList<T>? values,
        string parameterName)
    {
        if (values is null)
        {
            throw new ArgumentException($"{parameterName} must be an array.", parameterName);
        }

        return values;
    }

    private static void ValidateEnvironment(string environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new ArgumentException("Environment is required.", nameof(environment));
        }
    }

    private static string BuildPackageKey(CreateMetadataReleasePackageRequest request, DateTimeOffset now)
    {
        var basis = string.IsNullOrWhiteSpace(request.Title)
            ? $"metadata-release-{request.SourceEnvironment}-{now:yyyyMMddHHmmss}"
            : request.Title!;
        var builder = new StringBuilder(basis.Length);
        foreach (var ch in basis.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var result = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? $"metadata-release-{now:yyyyMMddHHmmss}" : result;
    }

    private sealed record ResolvedSemanticArtifact(
        string SemanticId,
        MetadataSemanticArtifactKind Kind,
        MetadataV2Resource? Resource = null,
        MetadataV2Resource? ParentResource = null,
        MetadataV2Field? Field = null,
        MetadataV2Service? Service = null,
        MetadataV2Publication? Publication = null,
        MetadataV2Catalog? Catalog = null,
        MetadataV2Policy? Policy = null,
        MetadataV2Role? Role = null,
        string? ContentVersionId = null,
        IReadOnlyList<ConsoleProvenanceRef>? ProvenanceRefs = null)
    {
        public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; } =
            ProvenanceRefs ?? Array.Empty<ConsoleProvenanceRef>();
    }
}

internal static partial class MetadataReleaseServiceLog
{
    [LoggerMessage(
        EventId = 116300,
        Level = LogLevel.Information,
        Message = "Metadata release inventory projected for environment {Environment} revision {Revision} with {EntryCount} entries.")]
    public static partial void InventoryProjected(
        ILogger logger,
        string environment,
        long revision,
        int entryCount);

    [LoggerMessage(
        EventId = 116301,
        Level = LogLevel.Information,
        Message = "Metadata release bindings resolved for {EnvironmentCount} environments and {SemanticIdCount} semantic ids with {BindingCount} bindings.")]
    public static partial void BindingsResolved(
        ILogger logger,
        int environmentCount,
        int semanticIdCount,
        int bindingCount);

    [LoggerMessage(
        EventId = 116302,
        Level = LogLevel.Information,
        Message = "Metadata release package {PackageId} created from {SourceEnvironment} revision {SourceRevision} for {TargetEnvironmentCount} targets and {EntryCount} entries.")]
    public static partial void PackageCreated(
        ILogger logger,
        Guid packageId,
        string sourceEnvironment,
        long sourceRevision,
        int targetEnvironmentCount,
        int entryCount);

    [LoggerMessage(
        EventId = 116303,
        Level = LogLevel.Information,
        Message = "Metadata release package {PackageId} exported as GitOps manifest with {EntryCount} entries.")]
    public static partial void ManifestExported(
        ILogger logger,
        Guid packageId,
        int entryCount);
}
