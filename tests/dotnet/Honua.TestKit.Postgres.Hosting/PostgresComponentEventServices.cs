// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Db.Postgres;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.TestKit.Postgres.Hosting;

internal static class PostgresComponentEventServices
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IGeometryService, GeometryService>();
        builder.Services.AddScoped<IGeometryValidator>(sp => new WkbGeometryValidator(
            sp.GetRequiredService<IOptions<LimitsOptions>>(),
            sp.GetRequiredService<IGeometryTopologyValidator>(),
            sp.GetRequiredService<ILogger<WkbGeometryValidator>>()));
        builder.Services.AddValidationServices();
        builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();
        builder.Services.AddScoped<IRowLevelSecurityFilterSource, RowLevelSecurityFilterSource>();
        builder.Services.AddScoped<IFieldMaskSource, FieldMaskSource>();
        builder.Services.AddSingleton<IETagService, ETagService>();
        builder.Services.AddSingleton<IResponseCache, MemoryResponseCache>();
        builder.Services.AddSingleton(sp => new OutputCacheInvalidationService(
            null, sp.GetRequiredService<IResponseCache>(), null,
            sp.GetRequiredService<IServiceScopeFactory>(), null,
            sp.GetRequiredService<ILogger<OutputCacheInvalidationService>>()));
        // Streaming transports, webhooks and background outbox delivery are verified by the full
        // host lanes; edits here reach the change log through the trigger alone.
        builder.Services.AddSingleton<IFeatureChangeEventPublisher, ChangeLogOnlyFeatureChangeEventPublisher>();
        builder.Services.AddSingleton<FeatureMutationEventService>();
    }
}
