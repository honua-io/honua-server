// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FieldWorkflows.Export;

public interface IFieldExportStore
{
    Task<FieldExportPackage> CreateExportAsync(
        FieldExportRequest request,
        string requestedBy,
        CancellationToken cancellationToken = default);

    Task<FieldExportRecordListResult> ListExportsAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default);
}
