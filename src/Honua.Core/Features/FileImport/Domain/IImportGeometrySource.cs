// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FileImport.Domain;

/// <summary>Reader-supplied geometry facts retained separately from user attributes during import.</summary>
public interface IImportGeometrySource
{
    /// <summary>Gets whether the source encoding declares Z, including empty or all-NaN coordinates.</summary>
    bool HasDeclaredZ { get; }

    /// <summary>Gets whether the source encoding declares measured ordinates.</summary>
    bool HasDeclaredM { get; }

    /// <summary>Gets whether edges follow straight Cartesian segments and must retain their planar coverage.</summary>
    bool HasPlanarEdges { get; }
}
