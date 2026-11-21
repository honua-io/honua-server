// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure.Security;

public sealed class AttachmentContentDispositionTests
{
    [UnitTheory]
    [InlineData("parcels_roads.csv")]
    [InlineData("Café_Zürich.gpkg")]
    [InlineData("東京_道路.zip")]
    [InlineData("name with spaces; and \\ backslash.json")]
    public void Create_IsAsciiAndCarriesTheOriginalName(string fileName)
    {
        var header = AttachmentContentDisposition.Create(fileName);

        // Kestrel refuses any response header character outside visible ASCII.
        Assert.All(header, c => Assert.InRange(c, ' ', '~'));
        var parsed = ContentDispositionHeaderValue.Parse(header);
        Assert.Equal("attachment", parsed.DispositionType);
        Assert.Equal(fileName, parsed.FileNameStar);
        Assert.NotNull(parsed.FileName);
    }

    [UnitTest]
    public void Create_RefusesAnEmptyName()
        => Assert.Throws<ArgumentException>(() => AttachmentContentDisposition.Create(" "));
}
