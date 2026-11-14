// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.OData.Services;

internal sealed record ODataCreateRequest(
    int LayerId, ParsedFeaturePayload Payload, string BaseUrl, int StorageLayerId,
    MetadataV2Object? Resource, IFeatureReader Reader);

internal sealed record ODataMutationTarget(
    int StorageLayerId, MetadataV2Object Resource, IFeatureReader Reader);

internal sealed record ODataUpdateRequest(
    int LayerId, long ObjectId, ParsedFeaturePayload Payload, string BaseUrl,
    string? IfMatch, string? IfNoneMatch, int StorageLayerId, bool Replace,
    MetadataV2Object? Resource, IFeatureReader Reader);

internal sealed record ODataDeleteRequest(
    int LayerId, long ObjectId, string? IfMatch, string? IfNoneMatch,
    int StorageLayerId, MetadataV2Object? Resource, IFeatureReader Reader);
