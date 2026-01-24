// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Services;

internal sealed class AdminApiOptions
{
    public const string SectionName = "AdminApi";

    public string? BaseUrl { get; init; }

    public string[] Scopes { get; init; } = ["honua.admin"];
}
