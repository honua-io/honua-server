// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Tests.Features.Admin;

internal sealed record TenantLayerSpec
{
    public required string Tenant { get; init; }

    public required string ServiceId { get; init; }

    public required string ResourceId { get; init; }

    public required string BindingId { get; init; }

    public required string PublicationId { get; init; }

    public required int StorageLayerId { get; init; }
}

internal static class TwoTenantGraphFixture
{
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";
    public const string ServiceA = "svc-a";
    public const string ServiceB = "svc-b";
    public const string ResourceA = "res-a";
    public const string ResourceB = "res-b";
    public const int StorageLayerA = 101;
    public const int StorageLayerB = 202;

    private static readonly TenantLayerSpec SpecA = new()
    {
        Tenant = TenantA,
        ServiceId = ServiceA,
        ResourceId = ResourceA,
        BindingId = "bind-a",
        PublicationId = "pub-a",
        StorageLayerId = StorageLayerA,
    };

    private static readonly TenantLayerSpec SpecB = new()
    {
        Tenant = TenantB,
        ServiceId = ServiceB,
        ResourceId = ResourceB,
        BindingId = "bind-b",
        PublicationId = "pub-b",
        StorageLayerId = StorageLayerB,
    };

    public static MetadataV2GraphSnapshot BuildSnapshot(string etag)
    {
        TenantLayerSpec[] specs = [SpecA, SpecB];
        return new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Revision = 1,
                Services = [.. specs.Select(CreateService)],
                Resources = [.. specs.Select(CreateResource)],
                StorageBindings = [.. specs.Select(CreateBinding)],
                Publications = [.. specs.Select(CreatePublication)],
            },
            etag,
            DateTimeOffset.UnixEpoch);
    }

    private static MetadataV2Service CreateService(TenantLayerSpec spec) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = spec.ServiceId, Name = spec.ServiceId, Tenant = spec.Tenant },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2Object CreateResource(TenantLayerSpec spec) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = spec.ResourceId, Name = spec.ResourceId, Tenant = spec.Tenant },
        StorageBindingIds = [spec.BindingId],
        PrimaryStorageBindingId = spec.BindingId,
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2StorageBinding CreateBinding(TenantLayerSpec spec) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = spec.BindingId, Name = spec.BindingId, Tenant = spec.Tenant },
        ResourceId = spec.ResourceId,
        StorageLayerId = spec.StorageLayerId,
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2Layer CreatePublication(TenantLayerSpec spec) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = spec.PublicationId, Name = spec.PublicationId, Tenant = spec.Tenant },
        ServiceId = spec.ServiceId,
        ResourceId = spec.ResourceId,
        StorageBindingId = spec.BindingId,
        LayerIndex = 0,
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };
}
