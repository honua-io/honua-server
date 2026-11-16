// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Console.Services;

// A create named an id that is already taken. It is the one store refusal a caller may read; any other failure
// is reported without its message, because a shared store's failures describe the database, not the request.
internal sealed class ConsoleContentIdConflictException(string message) : InvalidOperationException(message)
{
    public static ConsoleContentIdConflictException AlreadyExists(string id) => new($"Console content item '{id}' already exists.");

    public static ConsoleContentIdConflictException PublishedMetadata() => new("Published metadata is read-only in the content catalog.");
}
