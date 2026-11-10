// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface IDatabaseCompatibilityChecker
{
    Task<DatabaseCompatibilityResult> CheckCompatibilityAsync(
        string connectionString, CancellationToken cancellationToken = default);
}
