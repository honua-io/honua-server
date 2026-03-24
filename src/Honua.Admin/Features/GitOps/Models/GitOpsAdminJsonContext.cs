// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Admin.Features.GitOps.Models;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GitOpsWatchConfigSaveRequest), TypeInfoPropertyName = nameof(GitOpsWatchConfigSaveRequest))]
[JsonSerializable(typeof(ManifestApproveRequestModel), TypeInfoPropertyName = nameof(ManifestApproveRequestModel))]
[JsonSerializable(typeof(ManifestRejectRequestModel), TypeInfoPropertyName = nameof(ManifestRejectRequestModel))]
[JsonSerializable(typeof(ApiEnvelope<GitOpsWatchConfigModel>), TypeInfoPropertyName = nameof(GitOpsWatchConfigEnvelope))]
[JsonSerializable(typeof(ApiEnvelope<GitOpsChangeRecordModel[]>), TypeInfoPropertyName = nameof(GitOpsChangeListEnvelope))]
[JsonSerializable(typeof(ApiEnvelope<GitOpsChangeDiffModel>), TypeInfoPropertyName = nameof(GitOpsChangeDiffEnvelope))]
[JsonSerializable(typeof(ApiEnvelope<ManifestPendingChangeModel[]>), TypeInfoPropertyName = nameof(ManifestPendingListEnvelope))]
[JsonSerializable(typeof(ApiEnvelope<ManifestPendingChangeModel>), TypeInfoPropertyName = nameof(ManifestPendingEnvelope))]
internal sealed partial class GitOpsAdminJsonContext : JsonSerializerContext
{
}
