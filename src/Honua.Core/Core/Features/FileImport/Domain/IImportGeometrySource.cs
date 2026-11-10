// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FileImport.Domain;

public interface IImportGeometrySource
{
    bool HasDeclaredZ { get; }

    bool HasDeclaredM { get; }

    // Edges are straight Cartesian segments and must keep their planar coverage.
    bool HasPlanarEdges { get; }
}
