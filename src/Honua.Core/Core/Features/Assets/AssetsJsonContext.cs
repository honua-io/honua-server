// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Assets;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AssetProfileWriteRequest))]
[JsonSerializable(typeof(AssetProfile))]
[JsonSerializable(typeof(AssetProfileCommandResponse))]
[JsonSerializable(typeof(AssetProfileListResult))]
[JsonSerializable(typeof(AssetValidationProblem))]
[JsonSerializable(typeof(AssetCommandConflict))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(WorkTaskCreateRequest))]
[JsonSerializable(typeof(WorkTaskTransitionRequest))]
[JsonSerializable(typeof(WorkTask))]
[JsonSerializable(typeof(WorkTaskCommandResponse))]
[JsonSerializable(typeof(WorkTaskListResult))]
[JsonSerializable(typeof(WorkTaskHistoryResult))]
[JsonSerializable(typeof(WorkTaskCompletionRequest))]
public sealed partial class AssetsJsonContext : JsonSerializerContext;
