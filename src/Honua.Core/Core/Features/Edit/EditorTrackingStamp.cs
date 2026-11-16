// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

// Who and when for editor-tracking fields: the caller's name and the stored timestamp an edit
// protocol writes for a declared field. A null timestamp leaves that field unstamped.
public readonly record struct EditorTrackingStamp(string? PrincipalName, Func<MetadataV2Field?, object?> TimeValue);
