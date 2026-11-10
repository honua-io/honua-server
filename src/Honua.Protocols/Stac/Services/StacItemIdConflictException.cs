// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Stac.Services;

internal sealed class StacItemIdConflictException : Exception
{
    public StacItemIdConflictException(string message)
        : base(message)
    {
    }
}
