// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal;

public static class GdalContainerProbe
{
    public static string DefaultImage => GdalContainerExecutionOptions.DefaultImage;

    public static async Task<bool> IsImageAvailableAsync(
        string? image = null,
        string dockerExecutable = "docker",
        CancellationToken cancellationToken = default)
    {
        var invoker = new ProcessDockerCommandInvoker(NullLogger<ProcessDockerCommandInvoker>.Instance);
        return await invoker.ImageExistsAsync(
            dockerExecutable,
            string.IsNullOrWhiteSpace(image) ? GdalContainerExecutionOptions.DefaultImage : image,
            cancellationToken).ConfigureAwait(false);
    }
}
