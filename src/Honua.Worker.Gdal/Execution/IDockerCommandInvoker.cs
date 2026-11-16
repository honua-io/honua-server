// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

internal interface IDockerCommandInvoker
{
    Task<GdalCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken);

    Task<bool> ImageExistsAsync(
        string executable,
        string image,
        CancellationToken cancellationToken);
}
