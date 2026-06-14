// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.FieldWorkflows.Review;

/// <summary>
/// Source-generated JSON context for back-office field review contracts.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FieldSubmissionRecord))]
[JsonSerializable(typeof(FieldSubmissionRecord[]))]
[JsonSerializable(typeof(FieldReviewState))]
[JsonSerializable(typeof(FieldReviewComment))]
[JsonSerializable(typeof(FieldReviewComment[]))]
[JsonSerializable(typeof(FieldSubmissionListResult))]
[JsonSerializable(typeof(FieldReviewAssignmentRequest))]
[JsonSerializable(typeof(FieldReviewDecisionRequest))]
[JsonSerializable(typeof(FieldReviewCommentRequest))]
[JsonSerializable(typeof(FieldSubmissionDetail))]
[JsonSerializable(typeof(FieldSubmissionAttachmentInfo))]
[JsonSerializable(typeof(FieldSubmissionAttachmentInfo[]))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class FieldReviewJsonContext : JsonSerializerContext
{
}
