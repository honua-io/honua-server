// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Infrastructure.Helpers;

internal static class SecureXmlDocumentParser
{
    internal const int DefaultMaxElementDepth = RemoteXmlDocumentParser.DefaultMaxElementDepth;
    internal const long DefaultMaxCharactersInDocument = RemoteXmlDocumentParser.DefaultMaxCharactersInDocument;

    public static XDocument Parse(string xml, LoadOptions loadOptions = LoadOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        return RemoteXmlDocumentParser.ParseProhibitingDtd(xml, loadOptions);
    }
}
