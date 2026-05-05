// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Postgres.Features.Mobile.FieldCollection;

/// <summary>
/// Source-generated JSON context for persisting <see cref="FieldCollectionPushResult"/>
/// idempotency responses in the <c>fieldcollection_pushed_changes</c> table.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(FieldCollectionPushResult))]
internal sealed partial class FieldCollectionSyncStoreJsonContext : JsonSerializerContext
{
}
