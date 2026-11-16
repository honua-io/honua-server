// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

public sealed record FeatureProviderBinding(
    MetadataV2Service Service,
    MetadataV2Object Resource,
    MetadataV2Layer Publication,
    MetadataV2StorageBinding StorageBinding,
    FeatureStorageMapping StorageMapping,
    int StorageLayerId,
    IFeatureDataProvider Provider,
    DataConnection? Connection);
