// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Mobile.FieldCollection;

/// <summary>
/// Source-generated JSON context for FieldCollection mobile sync request and
/// response models (#894). Keeps the endpoints reflection-free and compatible
/// with Native AOT.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(FieldCollectionGenerationResponse))]
[JsonSerializable(typeof(FieldCollectionSyncCursorResponse))]
[JsonSerializable(typeof(FieldCollectionPullResponse))]
[JsonSerializable(typeof(FieldCollectionServerChange))]
[JsonSerializable(typeof(FieldCollectionServerChange[]))]
[JsonSerializable(typeof(FieldCollectionPushRequestModel))]
[JsonSerializable(typeof(FieldCollectionPushResponse))]
internal sealed partial class FieldCollectionSyncJsonContext : JsonSerializerContext
{
}
