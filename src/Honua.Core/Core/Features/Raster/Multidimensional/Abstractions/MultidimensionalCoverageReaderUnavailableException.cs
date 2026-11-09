// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Multidimensional.Abstractions;

public sealed class MultidimensionalCoverageReaderUnavailableException : InvalidOperationException
{
    public const string ProblemCode = "HONUA-COV-HDF-READER-NOT-ENABLED";

    public MultidimensionalCoverageReaderUnavailableException(string message)
        : base(message)
    {
    }
}

// The layout would require a whole-file read, so the reader refuses it.
public sealed class MultidimensionalCoverageUnsupportedLayoutException : InvalidOperationException
{
    public const string ProblemCode = "HONUA-COV-HDF-UNSUPPORTED-LAYOUT";

    public MultidimensionalCoverageUnsupportedLayoutException(string message)
        : base(message)
    {
    }

    public MultidimensionalCoverageUnsupportedLayoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
