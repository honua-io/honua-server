// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Worker.Gdal.Execution;

internal sealed class GdalContainerExecutionOptions
{
    public const string DefaultImage = "honua-worker-etl";

    public const string DefaultMemoryLimit = "2g";

    public const string DefaultCpuLimit = "2";

    public const int DefaultPidsLimit = 256;

    public string Image { get; set; } = DefaultImage;

    public string DockerExecutable { get; set; } = "docker";

    public string User { get; set; } = "1001:1001";

    public string Network { get; set; } = "none";

    public string RemoteVsiNetwork { get; set; } = "bridge";

    public string? MemoryLimit { get; set; } = DefaultMemoryLimit;

    public string? CpuLimit { get; set; } = DefaultCpuLimit;

    [Range(1, 65_536, ErrorMessage = "PidsLimit must be between 1 and 65536")]
    public int? PidsLimit { get; set; } = DefaultPidsLimit;
}
