// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Abstractions;

public sealed class TemporalLayerNotFoundException : Exception
{
    public TemporalLayerNotFoundException(string message) : base(message)
    {
    }
}

public sealed class TemporalNotSupportedException : Exception
{
    public TemporalNotSupportedException(string message) : base(message)
    {
    }
}

public sealed class TemporalValidationException : Exception
{
    public TemporalValidationException(string message) : base(message)
    {
    }
}

public sealed class TemporalRollbackNotPermittedException : Exception
{
    public TemporalRollbackNotPermittedException(string message) : base(message)
    {
    }
}
