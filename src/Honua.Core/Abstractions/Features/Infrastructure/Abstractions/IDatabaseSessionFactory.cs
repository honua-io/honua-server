// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;

namespace Honua.Core.Features.Infrastructure.Abstractions;

// The caller must dispose the session, because the session owns the connection.
public interface IDatabaseSessionFactory
{
    Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default);

    Task<IDatabaseSession> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);
}
