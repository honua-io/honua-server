-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.cdn_deployment_purges (
    cluster_arn TEXT COLLATE "C" NOT NULL,
    service_arn TEXT COLLATE "C" NOT NULL,
    deployment_id TEXT COLLATE "C" NOT NULL,
    task_definition_arn TEXT NOT NULL,
    event_id TEXT COLLATE "C" NOT NULL,
    purge_completed_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (cluster_arn, service_arn, deployment_id),
    UNIQUE (cluster_arn, service_arn, event_id)
);
