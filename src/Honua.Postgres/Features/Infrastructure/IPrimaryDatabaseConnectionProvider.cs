// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Marker interface for the base database connection provider before secure connection decoration.
/// </summary>
internal interface IPrimaryDatabaseConnectionProvider : IDatabaseConnectionProvider
{
}
