// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.AgentWorkspace;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SubmitDatasetImport(
    [property: JsonRequired] string TaskContentHash,
    [property: JsonRequired] long AuthorityRevision,
    [property: JsonRequired] string AuthorityFingerprint,
    [property: JsonRequired] string PolicyContextFingerprint);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SubmitDatasetImportCall(
    [property: JsonRequired] string WorkspaceId,
    [property: JsonRequired] string TaskId,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] SubmitDatasetImport Request);
