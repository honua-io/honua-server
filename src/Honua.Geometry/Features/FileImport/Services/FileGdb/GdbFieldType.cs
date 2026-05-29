// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.FileImport.Services.FileGdb;

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
