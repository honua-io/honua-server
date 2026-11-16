// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.TestKit.Infrastructure;

internal sealed class FixtureCurrentSchemaContext(Func<string?> currentSchema) : ISchemaContext
{
    public string? CurrentSchema => currentSchema();
}
