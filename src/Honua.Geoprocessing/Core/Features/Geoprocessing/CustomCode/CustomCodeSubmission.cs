// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Geoprocessing.CustomCode;

internal sealed record CustomCodeSubmission(
    string JobId,
    ClaimsPrincipal Principal,
    Dictionary<string, string> SpecParams,
    JobSecurityContext? SecurityContext = null);
