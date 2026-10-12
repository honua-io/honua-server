// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FileImport.Domain;

/// <summary>
/// Vocabulary for where a background file import runs (honua-server#5916). Reported as
/// <c>runner</c> on the import job document and on the <c>import.background</c> capability.
/// </summary>
public static class ImportJobRunners
{
    /// <summary>A batch-compute execution worker (for example AWS Batch) runs the import.</summary>
    public const string Batch = "batch";

    /// <summary>A long-lived server process runs the import (durable worker loop or legacy in-process runner).</summary>
    public const string InProcess = "in-process";

    /// <summary>This host cannot run background imports; it refuses them with a typed 503.</summary>
    public const string Unavailable = "unavailable";
}
