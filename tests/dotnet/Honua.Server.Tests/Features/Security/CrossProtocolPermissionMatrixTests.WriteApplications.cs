// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Features.Security;

public sealed partial class CrossProtocolPermissionMatrixTests
{
    // Write cases differ only in the grants that GrantedRole holds. One application per write
    // catalog serves all of them: each case replaces the role's grants before it sends its
    // request, and cases of a class run one at a time. The feature store is created per
    // request, so no edit outlives the case that made it.
    public sealed class WriteApplications : IDisposable
    {
        private readonly Lazy<SharedWriteApplication> _featureServer =
            new(static () => new SharedWriteApplication(new RbacTestLayerCatalog()));

        private readonly Lazy<SharedWriteApplication> _grpc =
            new(static () => new SharedWriteApplication(new GrpcWriteLayerCatalog()));

        public WebApplicationFactory<Program> FeatureServer(PermissionGrant[] grants)
            => _featureServer.Value.WithGrants(grants);

        public WebApplicationFactory<Program> Grpc(PermissionGrant[] grants)
            => _grpc.Value.WithGrants(grants);

        public void Dispose()
        {
            DisposeIfCreated(_featureServer);
            DisposeIfCreated(_grpc);
        }

        private static void DisposeIfCreated(Lazy<SharedWriteApplication> application)
        {
            if (application.IsValueCreated)
            {
                application.Value.Dispose();
            }
        }
    }

    private sealed class SharedWriteApplication : IDisposable
    {
        private readonly MatrixRoleStore _roleStore = new(GrantedRole, []);
        private readonly WebApplicationFactory<Program> _application;

        public SharedWriteApplication(ITestMetadataV2GraphSource catalog)
        {
            _application = CreateFactory(catalog, _roleStore);
        }

        public WebApplicationFactory<Program> WithGrants(PermissionGrant[] grants)
        {
            _roleStore.Grants = grants;
            return _application;
        }

        public void Dispose() => _application.Dispose();
    }
}
