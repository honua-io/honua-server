// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

public interface IStyleSuggestionService
{
    Task<StyleSuggestion> SuggestAsync(
        MetadataV2Object resource,
        int storageLayerId,
        StyleSuggestionOptions? options = null,
        CancellationToken cancellationToken = default);
}
