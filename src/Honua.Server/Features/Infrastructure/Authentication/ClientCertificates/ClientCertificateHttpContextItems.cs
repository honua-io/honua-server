// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateHttpContextItems
{
    public const string ExtractionResult = "__honua_client_certificate_extraction";
    public const string ValidationResult = "__honua_client_certificate_validation";
    public const string AuditEmitted = "__honua_client_certificate_audit_emitted";
    public const string OriginalProxyPeerIpAddress = "__honua_client_certificate_original_proxy_peer_ip";
}
