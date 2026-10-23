// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Features.Migration;

/// <summary>
/// JSON serialization context for import operations - AOT compatible
/// </summary>
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
// honua-server#4419: DateTime was the one temporal type missing here while DateTimeOffset,
// DateOnly, TimeOnly and TimeSpan were all registered (and Honua.Import's sibling context does
// register it). A shapefile's DBF date field surfaces as a System.DateTime, so serializing the
// property bag threw and EVERY row of such an import failed — while the response still reported
// success with featureCount 0 and a generic "rows failed" warning. No test read an imported
// shapefile back, so the loss was invisible.
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
internal sealed partial class ImportJsonContext : JsonSerializerContext
{
}
