// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Mobile.FieldCollection;

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(FieldCollectionGenerationResponse))]
[JsonSerializable(typeof(FieldCollectionSyncCursorResponse))]
[JsonSerializable(typeof(FieldCollectionSyncCursorAckRequest))]
[JsonSerializable(typeof(FieldCollectionPullResponse))]
[JsonSerializable(typeof(FieldCollectionServerChange))]
[JsonSerializable(typeof(FieldCollectionServerChange[]))]
[JsonSerializable(typeof(FieldCollectionPushRequestModel))]
[JsonSerializable(typeof(FieldCollectionPushResponse))]
internal sealed partial class FieldCollectionSyncJsonContext : JsonSerializerContext
{
}
