// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FileImport.Domain;

public static class ImportJobRunners
{
    public const string Batch = "batch";

    public const string InProcess = "in-process";

    // This host cannot run background imports and refuses them with a typed 503.
    public const string Unavailable = "unavailable";
}
