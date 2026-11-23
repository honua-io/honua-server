// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public sealed class SceneOutputUnwritableException : Exception
{
    public const string OperatorMessage =
        SceneGenerationErrorCodes.OutputUnwritable + ": Scene output could not be written. Configure SceneGeneration:OutputRoot on writable persistent storage and check permissions and free space.";

    public const string ObjectStoreOperatorMessage =
        SceneGenerationErrorCodes.AssetStoreUnwritable + ": Scene assets could not be written to the object store. Check the server's object-store permissions and FileStorage settings.";

    public SceneOutputUnwritableException(Exception innerException)
        : this(SceneGenerationErrorCodes.OutputUnwritable, OperatorMessage, innerException)
    {
    }

    private SceneOutputUnwritableException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    // Error code for job status; Message is the operator-facing text and never carries provider detail.
    public string Code { get; }

    public static SceneOutputUnwritableException ForObjectStore(Exception innerException)
        => new(SceneGenerationErrorCodes.AssetStoreUnwritable, ObjectStoreOperatorMessage, innerException);
}
