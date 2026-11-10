// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AutoDocs.Domain;

public sealed class MetadataDocumentResult
{
    public required string Iso19115Xml { get; init; }

    public required string FgdcXml { get; init; }

    public required string DataDictionary { get; init; }

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
