// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.FileImport.Services.FileGdb;

internal sealed class GdbField
{
    public required string Name { get; init; }

    public required string Alias { get; init; }

    public required GdbFieldType FieldType { get; init; }

    public bool IsNullable { get; init; }

    public int MaxLength { get; init; }

    public GdbGeometryDef? GeometryDef { get; init; }
}

internal sealed class GdbGeometryDef
{
    public string? WktSpatialReference { get; init; }

    public int Srid { get; init; }

    public double XOrigin { get; init; }

    public double YOrigin { get; init; }

    public double XYScale { get; init; }

    public double ZOrigin { get; init; }

    public double ZScale { get; init; }

    public double MOrigin { get; init; }

    public double MScale { get; init; }

    public double XYTolerance { get; init; }

    public bool HasZ { get; init; }

    public bool HasM { get; init; }
}
