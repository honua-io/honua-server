// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Collaboration.FeatureLocks;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    Converters =
    [
        typeof(JsonStringEnumConverter<FeatureLockClaimStatus>),
        typeof(JsonStringEnumConverter<FeatureLockRenewStatus>),
        typeof(JsonStringEnumConverter<FeatureLockReleaseStatus>)
    ])]
[JsonSerializable(typeof(FeatureLockMutationRequest))]
[JsonSerializable(typeof(FeatureRef))]
[JsonSerializable(typeof(LockHolder))]
[JsonSerializable(typeof(FeatureLockLease))]
[JsonSerializable(typeof(FeatureLockHeldError))]
[JsonSerializable(typeof(FeatureLockClaimResponse))]
[JsonSerializable(typeof(FeatureLockRenewResponse))]
[JsonSerializable(typeof(FeatureLockReleaseResponse))]
[JsonSerializable(typeof(ApiResponse<FeatureLockClaimResponse>))]
[JsonSerializable(typeof(ApiResponse<FeatureLockRenewResponse>))]
[JsonSerializable(typeof(ApiResponse<FeatureLockReleaseResponse>))]
internal sealed partial class FeatureLockJsonContext : JsonSerializerContext
{
}
