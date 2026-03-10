// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Postgres.Features.Import.FileGdb;

/// <summary>
/// Field types in a FileGDB .gdbtable file.
/// </summary>
internal enum GdbFieldType : byte
{
    Int16 = 0,
    Int32 = 1,
    Float32 = 2,
    Float64 = 3,
    String = 4,
    DateTime = 5,
    ObjectId = 6,
    Geometry = 7,
    Binary = 8,
    Raster = 9,
    Uuid = 10,
    GlobalId = 11,
    Xml = 12,
    Int64 = 13,
    DateTimeOffset = 14,
    TimeOnly = 15,
    DateOnly = 16,
}
