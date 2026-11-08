// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

public class ResourceAccessDeniedException : Exception
{
    public ResourceAccessDeniedException()
        : base("The operation is not permitted for this resource.")
    {
    }
}
