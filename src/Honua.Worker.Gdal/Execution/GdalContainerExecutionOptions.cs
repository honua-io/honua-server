// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

internal sealed class GdalContainerExecutionOptions
{
    public const string DefaultImage = "honua-worker-etl";

    public string Image { get; set; } = DefaultImage;

    public string DockerExecutable { get; set; } = "docker";

    public string User { get; set; } = "1001:1001";

    public string Network { get; set; } = "none";

    public string RemoteVsiNetwork { get; set; } = "bridge";
}
