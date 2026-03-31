# TLS Connection Guide

Honua uses Npgsql for PostgreSQL connectivity. This guide covers TLS/SSL configuration for managed and self-hosted PostgreSQL deployments.

## Npgsql SSL Mode Options

| SSL Mode | Description | Use Case |
|----------|-------------|----------|
| `Disable` | No encryption | Local development only |
| `Allow` | Prefer plaintext, accept TLS if required by server | Not recommended |
| `Prefer` | Prefer TLS, fall back to plaintext | Development environments |
| `Require` | Require TLS, skip certificate verification | Trusted network environments |
| `VerifyCA` | Require TLS, verify the server certificate CA | Managed services with custom CAs |
| `VerifyFull` | Require TLS, verify CA and hostname | Production (recommended) |

## AWS Aurora PostgreSQL

Aurora PostgreSQL supports and enforces TLS by default. Use `SSL Mode=VerifyFull` with the AWS RDS CA bundle for production deployments.

### Connection String Example

```
Host=my-cluster.cluster-abc123.us-east-1.rds.amazonaws.com;Port=5432;Database=honua;Username=honua_app;Password=<password>;SSL Mode=VerifyFull;Root Certificate=/path/to/aws-rds-ca-bundle.pem
```

### CA Certificate

Download the RDS CA bundle from the [AWS documentation](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/UsingWithRDS.SSL.html):

```bash
# Global bundle (all regions)
curl -o /etc/ssl/certs/aws-rds-ca-bundle.pem \
  https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem
```

For containers, mount the CA certificate as a volume or include it in the image.

## Azure Database for PostgreSQL

Azure Flexible Server uses DigiCert Global Root G2 as the certificate authority. Use `SSL Mode=VerifyFull` for production.

### Connection String Example

```
Host=my-server.postgres.database.azure.com;Port=5432;Database=honua;Username=honua_app;Password=<password>;SSL Mode=VerifyFull;Root Certificate=/path/to/DigiCertGlobalRootG2.crt.pem
```

### CA Certificate

Download the DigiCert Global Root G2 certificate:

```bash
curl -o /etc/ssl/certs/DigiCertGlobalRootG2.crt.pem \
  https://cacerts.digicert.com/DigiCertGlobalRootG2.crt.pem
```

Azure also supports the Microsoft RSA Root Certificate Authority 2017 for newer deployments. Check your server's SSL settings in the Azure portal.

## Self-Hosted PostgreSQL

For self-hosted deployments, configure TLS based on your security requirements:

| Environment | Recommended SSL Mode | Notes |
|-------------|---------------------|-------|
| Local development | `Disable` or `Prefer` | Encryption optional |
| Staging / Internal | `Require` | Encrypted, no certificate verification |
| Production | `VerifyFull` | Full verification with your CA |

### Self-Signed Certificates

If using self-signed certificates for internal deployments:

```
Host=pg-server.internal;Port=5432;Database=honua;Username=honua_app;Password=<password>;SSL Mode=VerifyFull;Root Certificate=/path/to/ca.crt;Trust Server Certificate=false
```

## Trust Store Paths by Platform

| Platform | Default CA Path |
|----------|----------------|
| Linux (Debian/Ubuntu) | `/etc/ssl/certs/` |
| Linux (RHEL/CentOS) | `/etc/pki/tls/certs/` |
| Alpine Linux | `/etc/ssl/certs/` |
| Windows | Windows Certificate Store (automatic) |
| macOS | System Keychain (automatic) |

For Docker containers, add the CA certificate to the container's trust store or reference it via the `Root Certificate` connection string parameter.

## Npgsql Connection String Reference

Key TLS-related parameters for Npgsql:

| Parameter | Description |
|-----------|-------------|
| `SSL Mode` | TLS mode (see table above) |
| `Root Certificate` | Path to CA certificate file |
| `Trust Server Certificate` | When `true`, skip certificate validation (not for production) |
| `Client Certificate` | Path to client certificate for mTLS |
| `Client Certificate Key` | Path to client certificate private key |

See the [Npgsql documentation](https://www.npgsql.org/doc/security.html) for the complete reference.
