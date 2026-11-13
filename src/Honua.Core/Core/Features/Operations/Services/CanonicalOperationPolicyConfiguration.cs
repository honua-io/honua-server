// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Policy;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Operations.Services;

public sealed record CanonicalOperationPolicyConfiguration(
    IOptions<OperationPolicyOptions> Options,
    bool ProductionEnvironment = false);
