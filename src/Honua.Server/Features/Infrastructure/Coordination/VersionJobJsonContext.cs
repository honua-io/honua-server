// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Infrastructure.Coordination;

/// <summary>
/// Source-generated JSON context for durably serializing <see cref="VersionJob"/> records into the
/// Redis-backed <see cref="RedisVersionJobStore"/> (#1553). AOT-friendly; no reflection on the hot path.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(VersionJob))]
internal sealed partial class VersionJobJsonContext : JsonSerializerContext
{
}
