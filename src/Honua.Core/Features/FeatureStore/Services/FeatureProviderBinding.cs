// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Runtime binding for a Metadata v2 publication, secure connection, storage mapping, and provider.
/// </summary>
/// <param name="Service">Metadata v2 service being resolved.</param>
/// <param name="Resource">Metadata v2 resource being resolved.</param>
/// <param name="Publication">Publication exposing the resource on the service.</param>
/// <param name="StorageBinding">Storage binding that materializes the resource.</param>
/// <param name="StorageMapping">Physical feature storage mapping resolved from the storage binding.</param>
/// <param name="StorageLayerId">Integer storage-layer handle used by feature-reader APIs.</param>
/// <param name="Provider">Resolved provider implementation.</param>
/// <param name="Connection">Secure connection used to select the provider, when any.</param>
public sealed record FeatureProviderBinding(
    MetadataV2Service Service,
    MetadataV2Resource Resource,
    MetadataV2Publication Publication,
    MetadataV2StorageBinding StorageBinding,
    FeatureStorageMapping StorageMapping,
    int StorageLayerId,
    IFeatureDataProvider Provider,
    DataConnection? Connection);
