// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AutoDocs.Domain;

namespace Honua.Core.Features.AutoDocs.Abstractions;

public interface IMetadataDocumentGenerator
{
    MetadataDocumentResult Generate(MetadataDocumentRequest request);
}
