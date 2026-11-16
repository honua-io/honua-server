// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Db.Postgres.Features.Migration;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(Honua.Core.Features.Migration.Domain.MigrationFidelityDifference[]))]
[JsonSerializable(typeof(Honua.Core.Features.Migration.Domain.GeoservicesImportTarget))]
internal sealed partial class MigrationBatchRunJsonContext : JsonSerializerContext;
