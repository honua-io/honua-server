// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security;

namespace Honua.Io.Export;

// Each run's directory has a random name and is created owner-only, so another local account
// cannot read a run's files while an export is built. Used by every export writer that spills.
internal static class ExportScratch
{
    public static void Create(string scratchDir) => PrivateTempFile.CreateDirectory(scratchDir);
}
