-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Security notifications commit atomically with their source writes. Transition tables bound
-- policy fan-out to one event per statement and membership fan-out to one event per principal.
-- No bearer material or protected token payload is copied into the realtime log.
CREATE OR REPLACE FUNCTION $HonuaSchema$.realtime_security_policy()
RETURNS TRIGGER LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    changed BOOLEAN;
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        changed := TRUE;
    ELSIF TG_OP = 'DELETE' THEN
        SELECT EXISTS (SELECT FROM old_security_rows) INTO changed;
    ELSE
        SELECT EXISTS (SELECT FROM new_security_rows) INTO changed;
    END IF;
    IF changed THEN
        PERFORM $HonuaSchema$.realtime_append('security', jsonb_build_array(jsonb_build_object(
            'scope_key', '*', 'kind', 'policy-changed',
            'payload', jsonb_build_object('kind', 'policy-changed', 'subject', '*'))));
    END IF;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.realtime_security_principal()
RETURNS TRIGGER LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    subjects TEXT[];
    events JSONB;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT array_agg(DISTINCT user_id ORDER BY user_id) INTO subjects FROM new_security_rows;
    ELSIF TG_OP = 'DELETE' THEN
        SELECT array_agg(DISTINCT user_id ORDER BY user_id) INTO subjects FROM old_security_rows;
    ELSE
        SELECT array_agg(user_id ORDER BY user_id) INTO subjects FROM (
            SELECT user_id FROM old_security_rows UNION SELECT user_id FROM new_security_rows
        ) AS changed;
    END IF;
    SELECT jsonb_agg(jsonb_build_object('scope_key', subject, 'kind', 'principal-changed',
        'payload', jsonb_build_object('kind', 'principal-changed', 'subject', subject)) ORDER BY subject)
    INTO events FROM unnest(subjects) AS subject;
    IF events IS NOT NULL THEN
        PERFORM $HonuaSchema$.realtime_append('security', events);
    END IF;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.realtime_security_admin_key()
RETURNS TRIGGER LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    subjects UUID[];
    events JSONB;
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT array_agg(id ORDER BY id) INTO subjects FROM old_security_rows
        WHERE expires_at IS NULL OR expires_at > now();
    ELSE
        SELECT array_agg(n.id ORDER BY n.id) INTO subjects
        FROM new_security_rows n JOIN old_security_rows o ON o.id = n.id
        WHERE (to_jsonb(n) - ARRAY['last_used_at', 'updated_at', 'name', 'created_by'])
            IS DISTINCT FROM (to_jsonb(o) - ARRAY['last_used_at', 'updated_at', 'name', 'created_by']);
    END IF;
    SELECT jsonb_agg(jsonb_build_object('scope_key', subject::TEXT, 'kind', 'credential-revoked',
        'payload', jsonb_build_object('kind', 'credential-revoked', 'subject', subject::TEXT,
            'credentialKind', 'AdminApiKey')) ORDER BY subject)
    INTO events FROM unnest(subjects) AS subject;
    IF events IS NOT NULL THEN
        PERFORM $HonuaSchema$.realtime_append('security', events);
    END IF;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.realtime_security_token()
RETURNS TRIGGER LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    events JSONB;
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT jsonb_agg(jsonb_build_object('scope_key', encode(token_hash, 'hex'), 'kind', 'credential-revoked',
            'payload', jsonb_build_object('kind', 'credential-revoked', 'subject', encode(token_hash, 'hex'),
                'credentialKind', kind)) ORDER BY kind, token_hash)
        INTO events FROM old_security_rows WHERE expires_at > now();
    ELSE
        -- The token store writes enum names and hashes the principal key for revocation markers.
        -- An upsert fires UPDATE when a marker already exists; both paths must invalidate streams.
        SELECT jsonb_agg(jsonb_build_object('scope_key', encode(token_hash, 'hex'), 'kind', 'credential-revoked',
            'payload', jsonb_build_object('kind', 'credential-revoked', 'subject', encode(token_hash, 'hex'),
                'credentialKind', kind)) ORDER BY token_hash)
        INTO events FROM new_security_rows
        WHERE kind = 'PortalPrincipalRevocation' AND expires_at > now();
    END IF;
    IF events IS NOT NULL THEN
        PERFORM $HonuaSchema$.realtime_append('security', events);
    END IF;
    RETURN NULL;
END;
$$;

-- A distinct trigger for each operation is required for PostgreSQL transition relations.
DO $$
DECLARE
    target TEXT;
    action TEXT;
    handler TEXT;
    relations TEXT;
    trigger_name TEXT;
    target_relation REGCLASS;
BEGIN
    FOREACH target IN ARRAY ARRAY[
        'rbac_roles', 'rbac_role_permissions', 'rbac_rls_policies', 'rbac_field_mask_policies',
        'portal_item_shares', 'portal_community_groups', 'rbac_role_memberships',
        'managed_users', 'managed_user_roles', 'admin_api_keys', 'security_tokens'
    ] LOOP
        -- Legacy RBAC stores predate configurable schemas and remain in honua.
        target_relation := CASE WHEN target LIKE 'rbac_%'
            THEN to_regclass(format('honua.%I', target))
            ELSE to_regclass(format('$HonuaSchema$.%I', target)) END;
        FOREACH action IN ARRAY ARRAY['INSERT', 'UPDATE', 'DELETE', 'TRUNCATE'] LOOP
            IF target = 'admin_api_keys' AND action = 'INSERT' THEN
                CONTINUE;
            END IF;
            handler := CASE
                WHEN action = 'TRUNCATE' THEN 'realtime_security_policy'
                WHEN target = 'rbac_role_memberships' THEN 'realtime_security_principal'
                WHEN target = 'admin_api_keys' THEN 'realtime_security_admin_key'
                WHEN target = 'security_tokens' THEN 'realtime_security_token'
                ELSE 'realtime_security_policy' END;
            relations := CASE action
                WHEN 'INSERT' THEN 'REFERENCING NEW TABLE AS new_security_rows'
                WHEN 'DELETE' THEN 'REFERENCING OLD TABLE AS old_security_rows'
                WHEN 'UPDATE' THEN 'REFERENCING OLD TABLE AS old_security_rows NEW TABLE AS new_security_rows'
                ELSE '' END;
            trigger_name := 'trg_rt_security_' || lower(action);
            EXECUTE format('CREATE OR REPLACE TRIGGER %I AFTER %s ON %s %s FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.%I()',
                trigger_name, action, target_relation, relations, handler);
        END LOOP;
    END LOOP;
END;
$$;
