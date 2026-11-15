// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Configuration;

namespace Honua.Server.Features.Console.Services;

// What every PostgreSQL Console store works through: the task's configuration documents, its clock, and the
// request whose captured configuration generation its reads honour.
internal sealed record ConsoleStateStorage(
    PostgresConfigurationDocuments Documents, TimeProvider Time, IHttpContextAccessor? Requests = null);
