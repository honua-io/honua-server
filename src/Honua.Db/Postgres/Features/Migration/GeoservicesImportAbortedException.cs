// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Migration;

/// <summary>
/// The ArcGIS importer stopped a job for a reason it states itself (for example a source that
/// makes no pagination progress). The message is authored by the importer, never provider or
/// HTTP text, so the job reports it as written (#4827).
/// </summary>
internal sealed class GeoservicesImportAbortedException : InvalidOperationException
{
    public GeoservicesImportAbortedException(string message)
        : base(message)
    {
    }
}
