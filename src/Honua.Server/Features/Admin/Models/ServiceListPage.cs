// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

internal sealed class ServiceListPage
{
    public required IReadOnlyList<ServiceSummary> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int Total { get; init; }

    public required int LayerTotal { get; init; }
}
