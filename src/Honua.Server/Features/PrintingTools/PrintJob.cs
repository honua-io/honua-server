// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// An async print job to be processed by the background service.
/// </summary>
internal sealed record PrintJob(
    string JobId,
    string WebMapJson,
    string Format,
    string TemplateName,
    int Dpi,
    ClaimsPrincipal? CallerPrincipal);
