// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed class DatabaseCompatibilityState
{
    private volatile DatabaseCompatibilityResult? _result;

    public DatabaseCompatibilityResult? Result => _result;

    public bool HasResult => _result is not null;

    public void SetResult(DatabaseCompatibilityResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }
}
