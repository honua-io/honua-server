// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.ControlPlane;

/// <summary>Readiness record published when a qualification barrier is reached.</summary>
internal sealed record QualificationReadyReceipt(
    string OperationId,
    string WorkerId,
    string Barrier,
    DateTimeOffset ReadyAt,
    int WorkerProcessId,
    int? ChildProcessId,
    bool ExecutorIgnoresCancellation);

/// <summary>Record published when the execution token is observed cancelled at a barrier.</summary>
internal sealed record QualificationSignalObservedReceipt(
    string OperationId,
    string WorkerId,
    string Barrier,
    DateTimeOffset ObservedAt,
    int WorkerProcessId,
    int? ChildProcessId,
    bool TokenCancelled);

/// <summary>Record published once the qualification runner releases a barrier.</summary>
internal sealed record QualificationReleasedReceipt(
    string OperationId,
    string WorkerId,
    string Barrier,
    DateTimeOffset ReleasedAt,
    int WorkerProcessId,
    int? ChildProcessId);

/// <summary>
/// Source-generated serialization for the qualification barrier receipts. The barrier runs
/// inside the trimmed/AOT-published worker, so these records may never go through the
/// reflection-based <see cref="JsonSerializer"/> overloads (IL2026/IL3050).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QualificationReadyReceipt))]
[JsonSerializable(typeof(QualificationSignalObservedReceipt))]
[JsonSerializable(typeof(QualificationReleasedReceipt))]
internal sealed partial class ExecutionQualificationReceiptJsonContext : JsonSerializerContext
{
}
