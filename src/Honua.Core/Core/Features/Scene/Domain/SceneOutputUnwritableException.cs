// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public sealed class SceneOutputUnwritableException : Exception
{
    public const string OperatorMessage =
        SceneGenerationErrorCodes.OutputUnwritable + ": Scene output could not be written. Configure SceneGeneration:OutputRoot on writable persistent storage and check permissions and free space.";

    public SceneOutputUnwritableException(Exception innerException)
        : base(OperatorMessage, innerException)
    {
    }
}
