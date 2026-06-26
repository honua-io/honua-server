// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.ControlPlane.Lambda;

/// <summary>
/// Source-generated JSON context for the EventBridge event types this Lambda deserializes. Keeps the
/// entrypoint AOT-safe (no reflection-based serialization) and is wired into the Lambda runtime via
/// <c>SourceGeneratorLambdaJsonSerializer</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(BatchJobStateChangeEvent))]
[JsonSerializable(typeof(BatchJobStateChangeDetail))]
[JsonSerializable(typeof(BackstopTickEvent))]
internal sealed partial class ControlPlaneLambdaJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Opaque payload type for the EventBridge Scheduler backstop tick. Its contents are ignored — the
/// handler simply sweeps once — but a concrete (non-Stream) deserialization target keeps the Lambda
/// bootstrap overload resolution unambiguous and AOT-safe.
/// </summary>
internal sealed class BackstopTickEvent
{
}
