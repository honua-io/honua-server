// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Jobs;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConsoleJobListResponse))]
[JsonSerializable(typeof(ConsoleJobSummary))]
[JsonSerializable(typeof(ConsoleJobDetail))]
[JsonSerializable(typeof(ConsoleJobLatestEvent))]
[JsonSerializable(typeof(ConsoleJobLinks))]
[JsonSerializable(typeof(ConsoleJobFailure))]
[JsonSerializable(typeof(ConsoleJobRetryPolicy))]
[JsonSerializable(typeof(ConsoleJobStage))]
[JsonSerializable(typeof(ConsoleJobStepsResponse))]
[JsonSerializable(typeof(ConsoleJobStep))]
[JsonSerializable(typeof(ConsoleJobStepArtifact))]
[JsonSerializable(typeof(ConsoleJobActionDescriptor))]
[JsonSerializable(typeof(ConsoleJobActionsResponse))]
[JsonSerializable(typeof(ConsoleJobLogPageResponse))]
[JsonSerializable(typeof(ConsoleJobLogEntry))]
[JsonSerializable(typeof(ConsoleJobArtifactPageResponse))]
[JsonSerializable(typeof(ConsoleJobArtifact))]
[JsonSerializable(typeof(ConsoleJobControlResponse))]
[JsonSerializable(typeof(ConsoleJobDiagnostics))]
[JsonSerializable(typeof(ConsoleJobDiagnosticIdentity))]
[JsonSerializable(typeof(ConsoleJobDiagnosticRelease))]
[JsonSerializable(typeof(ConsoleJobDiagnosticLifecycle))]
[JsonSerializable(typeof(ConsoleJobDiagnosticAttempt))]
[JsonSerializable(typeof(ConsoleJobDiagnosticRetry))]
[JsonSerializable(typeof(ConsoleJobDiagnosticWorker))]
[JsonSerializable(typeof(ConsoleJobDiagnosticFailure))]
[JsonSerializable(typeof(ConsoleJobDiagnosticEvents))]
[JsonSerializable(typeof(ConsoleJobDiagnosticArtifacts))]
[JsonSerializable(typeof(ConsoleJobDiagnosticEvidence))]
[JsonSerializable(typeof(McpJobDiagnosis))]
[JsonSerializable(typeof(McpJobDiagnosisObserved))]
[JsonSerializable(typeof(McpJobDiagnosisWorkerObserved))]
[JsonSerializable(typeof(McpJobDiagnosisFailureObserved))]
[JsonSerializable(typeof(McpJobDiagnosisInferred))]
[JsonSerializable(typeof(McpJobEvidence))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ConsoleJobJsonContext : JsonSerializerContext
{
}
