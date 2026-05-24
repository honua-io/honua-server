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
[JsonSerializable(typeof(ConsoleJobActionDescriptor))]
[JsonSerializable(typeof(ConsoleJobActionsResponse))]
[JsonSerializable(typeof(ConsoleJobLogPageResponse))]
[JsonSerializable(typeof(ConsoleJobLogEntry))]
[JsonSerializable(typeof(ConsoleJobArtifactPageResponse))]
[JsonSerializable(typeof(ConsoleJobArtifact))]
[JsonSerializable(typeof(ConsoleJobControlResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ConsoleJobJsonContext : JsonSerializerContext
{
}
