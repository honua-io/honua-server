// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Identity.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Source-generated JSON context for the Redis-backed control-plane IAM store payloads
/// (<see cref="ManagedUser"/> and <see cref="ScimGroup"/> records), keeping the durable
/// identity path AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ManagedUser))]
[JsonSerializable(typeof(ScimGroup))]
internal sealed partial class IamStoreJsonContext : JsonSerializerContext
{
}
