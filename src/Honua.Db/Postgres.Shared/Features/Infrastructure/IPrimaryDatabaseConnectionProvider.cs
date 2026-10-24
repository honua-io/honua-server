// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Marker interface for the base database connection provider before secure connection decoration.
/// </summary>
internal interface IPrimaryDatabaseConnectionProvider : IAdoNetDatabaseConnectionProvider
{
}
