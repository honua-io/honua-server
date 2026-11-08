// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

public sealed class ImportGeometryTooLargeException : Exception
{
    public ImportGeometryTooLargeException(string message)
        : base(message)
    {
    }

    public ImportGeometryTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
