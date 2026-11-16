// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OgcClassic.UnitTests.Wfs20;

internal static class Wfs20WriteTestHandler
{
    internal static Wfs20Handler Create(
        IMetadataV2GraphProvider provider, IFeatureWriter writer, IFeatureReader reader)
    {
        var queryServices = new Wfs20QueryServices(
            reader,
            Substitute.For<IGmlFeatureStore>(),
            provider,
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            new QueryProcessor(Substitute.For<IFilterExpressionTranslator>(), reader, NullLogger<QueryProcessor>.Instance, provider),
            Options.Create(new Wfs20Options()),
            Substitute.For<IConfiguration>());
        return Wfs20HandlerTestFactory.Create(queryServices, writer);
    }
}
