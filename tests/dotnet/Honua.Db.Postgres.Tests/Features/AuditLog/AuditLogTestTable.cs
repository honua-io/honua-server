// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Tests.Features.AuditLog;

// The audit_log columns every isolated-schema test table shares; each test adds its own constraints and rules.
internal static class AuditLogTestTable
{
    internal const string Columns = """
        audit_id         BIGSERIAL    PRIMARY KEY,
        timestamp        TIMESTAMPTZ  NOT NULL,
        event_type       VARCHAR(32)  NOT NULL,
        actor            VARCHAR(256) NOT NULL,
        actor_type       VARCHAR(32)  NOT NULL,
        actor_agent      VARCHAR(256),
        resource_type    VARCHAR(64)  NOT NULL,
        resource_id      VARCHAR(256),
        action           VARCHAR(128) NOT NULL,
        outcome          VARCHAR(16)  NOT NULL,
        correlation_id   VARCHAR(64)  NOT NULL,
        remote_ip        VARCHAR(64),
        user_agent       VARCHAR(512),
        details          TEXT         NOT NULL DEFAULT '',
        prev_hash        CHAR(64),
        """;
}
