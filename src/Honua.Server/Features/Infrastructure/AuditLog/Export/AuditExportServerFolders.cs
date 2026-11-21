// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ObjectStorage;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

// The audit S3 sink may share the installation bucket, so its prefix is never a built-in connection folder.
internal sealed class AuditExportServerFolders(IOptionsMonitor<AuditExportOptions> options) : IObjectStorageServerFolders
{
    public IEnumerable<string> Folders => [options.CurrentValue.S3.KeyPrefix];
}
