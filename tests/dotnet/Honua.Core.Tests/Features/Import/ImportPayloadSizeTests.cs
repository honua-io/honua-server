// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Import;

public sealed class ImportPayloadSizeTests
{
    [UnitTest]
    public void Resolve_TilePackageRoute_UsesMaxTilePackageSizeRatherThanEditPayload()
    {
        var options = new LimitsOptions();

        var resolved = ImportPayloadSize.Resolve("/api/v1/admin/import/tile-package", options);

        resolved.Should().Be(options.Imports.MaxTilePackageSize);
        resolved.Should().Be(2L * FileSizeConstants.OneGB);
        resolved.Should().NotBe(options.Edits.MaxPayloadSize);
    }

    [UnitTest]
    public void Resolve_UploadAndPreview_KeepTheirImportCeilings()
    {
        var options = new LimitsOptions();

        ImportPayloadSize.Resolve("/api/v1/admin/import/upload", options)
            .Should().Be(options.Imports.MaxImportSize);
        ImportPayloadSize.Resolve("/api/v1/admin/import/preview", options)
            .Should().Be(options.Imports.MaxPreviewSize);
        ImportPayloadSize.Resolve("/api/v1/admin/features/0/applyEdits", options)
            .Should().Be(options.Edits.MaxPayloadSize);
    }
}
