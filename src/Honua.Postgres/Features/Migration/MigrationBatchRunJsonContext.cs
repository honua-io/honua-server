// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Source-generated JSON context for <see cref="PostgresMigrationBatchRunCatalog"/> (#1253). The
/// catalog persists a batch child's <c>DependsOn</c> dependency list as a JSON text column; using a
/// source-generated <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> instead of
/// the reflection-based <c>JsonSerializer</c> overloads keeps the provider AOT/trim-safe (no IL2026 /
/// IL3050). Web defaults preserve the prior serializer behavior (a no-op for a string collection).
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class MigrationBatchRunJsonContext : JsonSerializerContext;
