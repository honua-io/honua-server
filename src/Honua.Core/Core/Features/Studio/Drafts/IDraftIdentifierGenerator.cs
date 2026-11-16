// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Drafts;

public interface IDraftIdentifierGenerator
{
    string NewIdentifier(string prefix);
}

public sealed class GuidDraftIdentifierGenerator : IDraftIdentifierGenerator
{
    public string NewIdentifier(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return string.Concat(prefix, "_", Guid.NewGuid().ToString("n"));
    }
}
