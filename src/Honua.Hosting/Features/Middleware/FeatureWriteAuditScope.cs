// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Middleware;

// The request scope the feature-write auditor stamps actors from, and the operator audit
// policy it reads on each write.
internal sealed record FeatureWriteAuditScope(IHttpContextAccessor HttpContextAccessor, Func<AuditPolicyOptions> Policy);
