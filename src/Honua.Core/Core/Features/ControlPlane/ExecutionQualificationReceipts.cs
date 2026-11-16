// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.ControlPlane;

internal sealed record QualificationReadyReceipt(
    string OperationId,
    string WorkerId,
    string Barrier,
    DateTimeOffset ReadyAt,
    int WorkerProcessId,
    int? ChildProcessId,
    bool ExecutorIgnoresCancellation);

internal sealed record QualificationSignalObservedReceipt(
    string OperationId,
    string WorkerId,
    string Barrier,
    DateTimeOffset ObservedAt,
    int WorkerProcessId,
    int? ChildProcessId,
    bool TokenCancelled);

internal sealed record QualificationReleasedReceipt(
    string OperationId,
    string WorkerId,
    string Barrier,
    DateTimeOffset ReleasedAt,
    int WorkerProcessId,
    int? ChildProcessId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QualificationReadyReceipt))]
[JsonSerializable(typeof(QualificationSignalObservedReceipt))]
[JsonSerializable(typeof(QualificationReleasedReceipt))]
internal sealed partial class ExecutionQualificationReceiptJsonContext : JsonSerializerContext
{
}
