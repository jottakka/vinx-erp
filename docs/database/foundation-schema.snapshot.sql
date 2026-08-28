


SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

-- Name: audit; Type: SCHEMA; Schema: -; Owner: -

CREATE SCHEMA audit;


-- Name: foundation; Type: SCHEMA; Schema: -; Owner: -

CREATE SCHEMA foundation;


-- Name: append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb); Type: FUNCTION; Schema: audit; Owner: -

CREATE FUNCTION audit.append_event(p_actor_user_id uuid, p_scope text, p_requested_company_id uuid, p_source text, p_action text, p_resource_type text, p_resource_id uuid, p_outcome text, p_correlation_id uuid, p_reason text, p_metadata jsonb) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'audit', 'pg_temp'
    AS $$
BEGIN
    IF p_scope IS NULL OR p_scope NOT IN ('global', 'company') THEN
        RAISE EXCEPTION 'audit scope is invalid'
            USING ERRCODE = '23514';
    END IF;

    IF (p_scope = 'global' AND p_requested_company_id IS NOT NULL)
        OR (p_scope = 'company' AND p_requested_company_id IS NULL) THEN
        RAISE EXCEPTION 'audit scope and requested company are incompatible'
            USING ERRCODE = '23514';
    END IF;

    INSERT INTO audit_events (
        id, actor_user_id, scope, requested_company_id, source, action,
        resource_type, resource_id, outcome, correlation_id, reason,
        metadata_json, occurred_at)
    VALUES (
        gen_random_uuid(), p_actor_user_id, p_scope, p_requested_company_id,
        p_source, p_action, p_resource_type, p_resource_id, p_outcome,
        p_correlation_id, p_reason, p_metadata, clock_timestamp());
END;
$$;


-- Name: reject_audit_event_mutation(); Type: FUNCTION; Schema: audit; Owner: -

CREATE FUNCTION audit.reject_audit_event_mutation() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'audit', 'pg_catalog'
    AS $$
BEGIN
    IF current_user <> 'vinx_audit_owner' THEN
        RAISE EXCEPTION 'audit events are append-only'
            USING ERRCODE = '42501';
    END IF;
    RETURN OLD;
END;
$$;


-- Name: accept_member_invitation_delivery(uuid, uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.accept_member_invitation_delivery(p_invitation_id uuid, p_correlation_id uuid) RETURNS TABLE(status text, provider_pending_at timestamp with time zone, transitioned boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_company_id uuid;
    v_membership_id uuid;
    v_status text;
    v_provider_pending_at timestamp with time zone;
    v_membership_company_id uuid;
    v_actor_user_id uuid;
BEGIN
    IF p_invitation_id IS NULL OR p_correlation_id IS NULL THEN
        RAISE EXCEPTION 'member invitation delivery parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT invitation.company_id,
        invitation.membership_id,
        invitation.status,
        invitation.provider_pending_at
    INTO v_company_id,
        v_membership_id,
        v_status,
        v_provider_pending_at
    FROM foundation.pending_member_invitations AS invitation
    WHERE invitation.id = p_invitation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, NULL::timestamp with time zone, false;
        RETURN;
    END IF;

    SELECT membership.company_id,
        membership.created_by_user_id
    INTO v_membership_company_id,
        v_actor_user_id
    FROM foundation.memberships AS membership
    WHERE membership.id = v_membership_id
      AND membership.deleted_at IS NULL;

    IF NOT FOUND
        OR v_membership_company_id IS DISTINCT FROM v_company_id THEN
        RAISE EXCEPTION 'member invitation membership is inconsistent'
            USING ERRCODE = '23514';
    END IF;

    IF v_status = 'pending_delivery' THEN
        v_provider_pending_at := pg_catalog.clock_timestamp();
        UPDATE foundation.pending_member_invitations AS invitation
        SET status = 'pending_provider',
            provider_pending_at = v_provider_pending_at
        WHERE invitation.id = p_invitation_id
          AND invitation.status = 'pending_delivery';

        IF FOUND THEN
            PERFORM audit.append_event(
                v_actor_user_id,
                'company',
                v_company_id,
                'worker',
                'member_invitation.delivery.ingress',
                'member_invitation',
                p_invitation_id,
                'success',
                p_correlation_id,
                NULL,
                '{}'::jsonb);
            RETURN QUERY SELECT 'pending_provider'::text, v_provider_pending_at, true;
        ELSE
            RETURN QUERY SELECT v_status, v_provider_pending_at, false;
        END IF;
        RETURN;
    END IF;

    RETURN QUERY SELECT v_status, v_provider_pending_at, false;
END;
$$;


-- Name: accept_owner_invitation_delivery(uuid, uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.accept_owner_invitation_delivery(p_invitation_id uuid, p_correlation_id uuid) RETURNS TABLE(status text, provider_pending_at timestamp with time zone, transitioned boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_company_id uuid;
    v_invitation_user_id uuid;
    v_receipt_company_id uuid;
    v_receipt_owner_user_id uuid;
    v_actor_user_id uuid;
    v_status text;
    v_provider_pending_at timestamp with time zone;
BEGIN
    IF p_invitation_id IS NULL OR p_correlation_id IS NULL THEN
        RAISE EXCEPTION 'owner invitation delivery parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT invitation.company_id,
        invitation.user_id,
        invitation.status,
        invitation.provider_pending_at
    INTO v_company_id,
        v_invitation_user_id,
        v_status,
        v_provider_pending_at
    FROM foundation.pending_owner_invitations AS invitation
    WHERE invitation.id = p_invitation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, NULL::timestamp with time zone, false;
        RETURN;
    END IF;

    SELECT receipt.company_id,
        receipt.owner_user_id,
        receipt.actor_user_id
    INTO v_receipt_company_id,
        v_receipt_owner_user_id,
        v_actor_user_id
    FROM foundation.organization_provisioning_receipts AS receipt
    WHERE receipt.owner_invitation_id = p_invitation_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'owner invitation provisioning receipt is missing'
            USING ERRCODE = '23514';
    END IF;

    IF v_company_id IS DISTINCT FROM v_receipt_company_id THEN
        RAISE EXCEPTION 'owner invitation receipt company is inconsistent'
            USING ERRCODE = '23514';
    END IF;

    IF v_invitation_user_id IS DISTINCT FROM v_receipt_owner_user_id THEN
        RAISE EXCEPTION 'owner invitation receipt owner is inconsistent'
            USING ERRCODE = '23514';
    END IF;

    IF v_status = 'pending_delivery' THEN
        v_provider_pending_at := pg_catalog.clock_timestamp();
        UPDATE foundation.pending_owner_invitations AS invitation
        SET status = 'pending_provider',
            provider_pending_at = v_provider_pending_at
        WHERE invitation.id = p_invitation_id
          AND invitation.status = 'pending_delivery';

        IF FOUND THEN
            PERFORM audit.append_event(
                v_actor_user_id,
                'company',
                v_company_id,
                'worker',
                'owner_invitation.delivery.ingress',
                'owner_invitation',
                p_invitation_id,
                'success',
                p_correlation_id,
                NULL,
                '{}'::jsonb);
            RETURN QUERY SELECT 'pending_provider'::text, v_provider_pending_at, true;
        ELSE
            RETURN QUERY SELECT v_status, v_provider_pending_at, false;
        END IF;
        RETURN;
    END IF;

    RETURN QUERY SELECT v_status, v_provider_pending_at, false;
END;
$$;


-- Name: bootstrap_platform_operator(text, text, text, text, uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.bootstrap_platform_operator(p_subject text, p_issuer text, p_display_name text, p_provider_email text, p_correlation_id uuid) RETURNS TABLE(status text, user_id uuid)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_deployment_actor_id constant uuid := '00000000-0000-0000-0000-000000000001';
    v_operator_user_id uuid;
    v_operator_identity_id uuid;
    v_identity_user_id uuid;
    v_identity_id uuid;
    v_email_user_id uuid;
BEGIN
    IF p_subject IS NULL
        OR btrim(p_subject) = ''
        OR char_length(p_subject) > 400
        OR p_issuer IS NULL
        OR btrim(p_issuer) = ''
        OR char_length(p_issuer) > 200
        OR p_display_name IS NULL
        OR btrim(p_display_name) = ''
        OR char_length(p_display_name) > 200
        OR p_provider_email IS NULL
        OR btrim(p_provider_email) = ''
        OR char_length(p_provider_email) > 320
        OR p_correlation_id IS NULL THEN
        RAISE EXCEPTION 'platform operator bootstrap parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    LOCK TABLE foundation.platform_operators IN SHARE ROW EXCLUSIVE MODE;

    SELECT platform_operator.external_identity_id, identity_record.user_id
    INTO v_operator_identity_id, v_operator_user_id
    FROM foundation.platform_operators AS platform_operator
    INNER JOIN foundation.external_identities AS identity_record
        ON identity_record.id = platform_operator.external_identity_id
    WHERE platform_operator.singleton = 1;

    IF FOUND THEN
        SELECT identity_record.id, identity_record.user_id
        INTO v_identity_id, v_identity_user_id
        FROM foundation.external_identities AS identity_record
        WHERE identity_record.issuer = p_issuer
          AND identity_record.subject = p_subject;

        IF FOUND AND v_identity_id = v_operator_identity_id THEN
            RETURN QUERY SELECT 'already_configured'::text, v_operator_user_id;
            RETURN;
        END IF;

        PERFORM audit.append_event(
            v_deployment_actor_id,
            'global',
            NULL,
            'worker',
            'platform.operator.bootstrap',
            'platform_operator',
            NULL,
            'denied',
            p_correlation_id,
            'deployment_bootstrap_conflict',
            '{}'::jsonb);
        RETURN QUERY SELECT 'conflict'::text, NULL::uuid;
        RETURN;
    END IF;

    SELECT identity_record.id, identity_record.user_id
    INTO v_identity_id, v_identity_user_id
    FROM foundation.external_identities AS identity_record
    WHERE identity_record.issuer = p_issuer
      AND identity_record.subject = p_subject;

    IF FOUND THEN
        v_operator_user_id := v_identity_user_id;
    ELSE
        SELECT user_record.id
        INTO v_email_user_id
        FROM foundation.users AS user_record
        WHERE lower(user_record.email) = lower(p_provider_email);

        IF FOUND THEN
            PERFORM audit.append_event(
                v_deployment_actor_id,
                'global',
                NULL,
                'worker',
                'platform.operator.bootstrap',
                'platform_operator',
                NULL,
                'denied',
                p_correlation_id,
                'deployment_bootstrap_conflict',
                '{}'::jsonb);
            RETURN QUERY SELECT 'conflict'::text, NULL::uuid;
            RETURN;
        END IF;

        v_operator_user_id := gen_random_uuid();
        v_identity_id := gen_random_uuid();
        INSERT INTO foundation.users (id, display_name, email, created_at)
        VALUES (
            v_operator_user_id,
            btrim(p_display_name),
            btrim(p_provider_email),
            clock_timestamp());
        INSERT INTO foundation.external_identities (
            id,
            user_id,
            issuer,
            subject,
            created_at)
        VALUES (
            v_identity_id,
            v_operator_user_id,
            p_issuer,
            p_subject,
            clock_timestamp());
    END IF;

    INSERT INTO foundation.platform_operators (singleton, external_identity_id)
    VALUES (1, v_identity_id);

    PERFORM audit.append_event(
        v_deployment_actor_id,
        'global',
        NULL,
        'worker',
        'platform.operator.bootstrap',
        'user',
        v_operator_user_id,
        'success',
        p_correlation_id,
        'deployment_bootstrap',
        '{}'::jsonb);
    RETURN QUERY SELECT 'created'::text, v_operator_user_id;
END;
$$;


-- Name: claim_member_invitation_provider_delivery(uuid, uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.claim_member_invitation_provider_delivery(p_invitation_id uuid, p_correlation_id uuid) RETURNS TABLE(status text, attempt_id uuid, member_email text, claimed boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_company_id uuid;
    v_invitation_user_id uuid;
    v_membership_id uuid;
    v_membership_company_id uuid;
    v_status text;
    v_attempt_id uuid;
    v_member_email text;
BEGIN
    IF p_invitation_id IS NULL OR p_correlation_id IS NULL THEN
        RAISE EXCEPTION 'member invitation provider delivery parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT invitation.company_id,
        invitation.user_id,
        invitation.membership_id,
        invitation.status,
        invitation.provider_attempt_id
    INTO v_company_id,
        v_invitation_user_id,
        v_membership_id,
        v_status,
        v_attempt_id
    FROM foundation.pending_member_invitations AS invitation
    WHERE invitation.id = p_invitation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, NULL::uuid, NULL::text, false;
        RETURN;
    END IF;

    SELECT membership.company_id
    INTO v_membership_company_id
    FROM foundation.memberships AS membership
    WHERE membership.id = v_membership_id
      AND membership.deleted_at IS NULL;

    IF NOT FOUND
        OR v_membership_company_id IS DISTINCT FROM v_company_id THEN
        RAISE EXCEPTION 'member invitation membership is inconsistent'
            USING ERRCODE = '23514';
    END IF;

    IF v_status = 'pending_provider' THEN
        v_attempt_id := gen_random_uuid();
        UPDATE foundation.pending_member_invitations AS invitation
        SET status = 'provider_in_flight',
            provider_attempt_id = v_attempt_id,
            provider_started_at = pg_catalog.clock_timestamp()
        WHERE invitation.id = p_invitation_id
          AND invitation.status = 'pending_provider';

        IF NOT FOUND THEN
            RAISE EXCEPTION 'member invitation provider claim lost its row lock'
                USING ERRCODE = '40001';
        END IF;

        SELECT user_record.email
        INTO v_member_email
        FROM foundation.users AS user_record
        WHERE user_record.id = v_invitation_user_id;

        IF NOT FOUND OR v_member_email IS NULL OR btrim(v_member_email) = '' THEN
            RAISE EXCEPTION 'member invitation user email is unavailable'
                USING ERRCODE = '23514';
        END IF;

        RETURN QUERY SELECT 'provider_in_flight'::text, v_attempt_id, v_member_email, true;
        RETURN;
    END IF;

    RETURN QUERY SELECT v_status, v_attempt_id, NULL::text, false;
END;
$$;


-- Name: claim_owner_invitation_provider_delivery(uuid, uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.claim_owner_invitation_provider_delivery(p_invitation_id uuid, p_correlation_id uuid) RETURNS TABLE(status text, attempt_id uuid, owner_email text, claimed boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_company_id uuid;
    v_invitation_user_id uuid;
    v_receipt_company_id uuid;
    v_receipt_owner_user_id uuid;
    v_status text;
    v_attempt_id uuid;
    v_owner_email text;
BEGIN
    IF p_invitation_id IS NULL OR p_correlation_id IS NULL THEN
        RAISE EXCEPTION 'owner invitation provider delivery parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT invitation.company_id,
        invitation.user_id,
        invitation.status,
        invitation.provider_attempt_id
    INTO v_company_id,
        v_invitation_user_id,
        v_status,
        v_attempt_id
    FROM foundation.pending_owner_invitations AS invitation
    WHERE invitation.id = p_invitation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, NULL::uuid, NULL::text, false;
        RETURN;
    END IF;

    SELECT receipt.company_id, receipt.owner_user_id
    INTO v_receipt_company_id, v_receipt_owner_user_id
    FROM foundation.organization_provisioning_receipts AS receipt
    WHERE receipt.owner_invitation_id = p_invitation_id;

    IF NOT FOUND
        OR v_company_id IS DISTINCT FROM v_receipt_company_id
        OR v_invitation_user_id IS DISTINCT FROM v_receipt_owner_user_id THEN
        RAISE EXCEPTION 'owner invitation provisioning receipt is inconsistent'
            USING ERRCODE = '23514';
    END IF;

    IF v_status = 'pending_provider' THEN
        v_attempt_id := gen_random_uuid();
        UPDATE foundation.pending_owner_invitations AS invitation
        SET status = 'provider_in_flight',
            provider_attempt_id = v_attempt_id,
            provider_started_at = pg_catalog.clock_timestamp()
        WHERE invitation.id = p_invitation_id
          AND invitation.status = 'pending_provider';

        IF NOT FOUND THEN
            RAISE EXCEPTION 'owner invitation provider claim lost its row lock'
                USING ERRCODE = '40001';
        END IF;

        SELECT user_record.email
        INTO v_owner_email
        FROM foundation.users AS user_record
        WHERE user_record.id = v_invitation_user_id;

        IF NOT FOUND OR v_owner_email IS NULL OR btrim(v_owner_email) = '' THEN
            RAISE EXCEPTION 'owner invitation user email is unavailable'
                USING ERRCODE = '23514';
        END IF;

        RETURN QUERY SELECT 'provider_in_flight'::text, v_attempt_id, v_owner_email, true;
        RETURN;
    END IF;

    RETURN QUERY SELECT v_status, v_attempt_id, NULL::text, false;
END;
$$;


-- Name: create_employee_directory_entry(uuid, uuid, text, text, text); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.create_employee_directory_entry(p_request_id uuid, p_company_id uuid, p_display_name text, p_email text, p_payload_hash text) RETURNS TABLE(status text, employee_id uuid, membership_id uuid, user_id uuid)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $_$
DECLARE
    v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
    v_correlation_id uuid := COALESCE(
        NULLIF(current_setting('app.correlation_id', true), '')::uuid,
        gen_random_uuid());
    v_existing foundation.idempotency_records%ROWTYPE;
    v_user_id uuid := gen_random_uuid();
    v_membership_id uuid := gen_random_uuid();
    v_employee_id uuid := gen_random_uuid();
    v_now timestamp with time zone := clock_timestamp();
BEGIN
    IF p_company_id IS NULL
        OR p_request_id IS NULL
        OR p_payload_hash !~ '^[0-9a-f]{64}$'
        OR p_display_name IS NULL
        OR btrim(p_display_name) = ''
        OR char_length(btrim(p_display_name)) > 200
        OR p_email IS NULL
        OR btrim(p_email) = ''
        OR char_length(btrim(p_email)) > 320 THEN
        RAISE EXCEPTION 'employee directory parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    PERFORM set_config('app.company_id', p_company_id::text, true);

    IF v_actor_user_id IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM foundation.memberships AS membership
            INNER JOIN foundation.role_assignments AS assignment
                ON assignment.company_id = membership.company_id
               AND assignment.membership_id = membership.id
               AND assignment.deleted_at IS NULL
            INNER JOIN foundation.roles AS role_record
                ON role_record.company_id = assignment.company_id
               AND role_record.id = assignment.role_id
               AND role_record.deleted_at IS NULL
            INNER JOIN foundation.role_capabilities AS grant_record
                ON grant_record.company_id = assignment.company_id
               AND grant_record.role_id = assignment.role_id
               AND grant_record.scope = 'company'
               AND grant_record.deleted_at IS NULL
            INNER JOIN foundation.capabilities AS capability
                ON capability.id = grant_record.capability_id
               AND capability.name = 'foundation.employees.create'
            WHERE membership.company_id = p_company_id
              AND membership.user_id = v_actor_user_id
              AND membership.deleted_at IS NULL) THEN
        RAISE EXCEPTION 'employee directory capability is required'
            USING ERRCODE = '42501';
    END IF;

    PERFORM pg_advisory_xact_lock(
        hashtextextended(
            v_actor_user_id::text || '|' || p_company_id::text || '|' || p_request_id::text,
            0));
    SELECT * INTO v_existing
    FROM foundation.idempotency_records
    WHERE company_id = p_company_id
      AND actor_user_id = v_actor_user_id
      AND command_name = 'foundation.employee.create'
      AND request_id = p_request_id;
    IF FOUND THEN
        RETURN QUERY
        SELECT
            CASE
                WHEN v_existing.payload_hash = p_payload_hash THEN 'replay'
                ELSE 'idempotency_conflict'
            END,
            (v_existing.result_json ->> 'employeeId')::uuid,
            (v_existing.result_json ->> 'membershipId')::uuid,
            (v_existing.result_json ->> 'userId')::uuid;
        RETURN;
    END IF;

    INSERT INTO foundation.users (id, display_name, email, created_at)
    VALUES (v_user_id, btrim(p_display_name), lower(btrim(p_email)), v_now);
    INSERT INTO foundation.memberships (
        id, company_id, user_id, created_by_user_id, updated_by_user_id,
        request_id, created_at, updated_at)
    VALUES (
        v_membership_id, p_company_id, v_user_id, v_actor_user_id, v_actor_user_id,
        p_request_id, v_now, v_now);
    INSERT INTO foundation.employees (
        id, company_id, membership_id, display_name, created_by_user_id,
        updated_by_user_id, request_id, created_at, updated_at)
    VALUES (
        v_employee_id, p_company_id, v_membership_id, btrim(p_display_name),
        v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
    INSERT INTO foundation.idempotency_records (
        id, company_id, actor_user_id, command_name, request_id,
        payload_hash, result_json, created_at)
    VALUES (
        gen_random_uuid(), p_company_id, v_actor_user_id, 'foundation.employee.create',
        p_request_id, p_payload_hash,
        jsonb_build_object(
            'employeeId', v_employee_id,
            'membershipId', v_membership_id,
            'userId', v_user_id),
        v_now);
    PERFORM audit.append_event(
        v_actor_user_id,
        'company',
        p_company_id,
        'web',
        'foundation.employee.create',
        'employee',
        v_employee_id,
        'success',
        v_correlation_id,
        NULL,
        '{}'::jsonb);
    RETURN QUERY SELECT 'created'::text, v_employee_id, v_membership_id, v_user_id;
END;
$_$;


-- Name: create_pending_member_invitation(uuid, uuid, text, text, text); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.create_pending_member_invitation(p_request_id uuid, p_company_id uuid, p_display_name text, p_email text, p_payload_hash text) RETURNS TABLE(status text, membership_id uuid, user_id uuid, employee_id uuid, invitation_id uuid)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $_$
DECLARE
    v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
    v_correlation_id uuid := COALESCE(
        NULLIF(current_setting('app.correlation_id', true), '')::uuid,
        gen_random_uuid());
    v_existing foundation.idempotency_records%ROWTYPE;
    v_user_id uuid := gen_random_uuid();
    v_membership_id uuid := gen_random_uuid();
    v_employee_id uuid := gen_random_uuid();
    v_invitation_id uuid;
    v_now timestamp with time zone := clock_timestamp();
BEGIN
    IF p_company_id IS NULL
        OR p_request_id IS NULL
        OR p_payload_hash !~ '^[0-9a-f]{64}$'
        OR p_display_name IS NULL
        OR btrim(p_display_name) = ''
        OR char_length(btrim(p_display_name)) > 200
        OR p_email IS NULL
        OR btrim(p_email) = ''
        OR char_length(btrim(p_email)) > 320 THEN
        RAISE EXCEPTION 'member invitation parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    PERFORM set_config('app.company_id', p_company_id::text, true);

    IF v_actor_user_id IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM foundation.memberships AS membership
            INNER JOIN foundation.role_assignments AS assignment
                ON assignment.company_id = membership.company_id
               AND assignment.membership_id = membership.id
               AND assignment.deleted_at IS NULL
            INNER JOIN foundation.roles AS role_record
                ON role_record.company_id = assignment.company_id
               AND role_record.id = assignment.role_id
               AND role_record.deleted_at IS NULL
            INNER JOIN foundation.role_capabilities AS grant_record
                ON grant_record.company_id = assignment.company_id
               AND grant_record.role_id = assignment.role_id
               AND grant_record.scope = 'company'
               AND grant_record.deleted_at IS NULL
            INNER JOIN foundation.capabilities AS capability
                ON capability.id = grant_record.capability_id
               AND capability.name = 'foundation.roles.assign'
            WHERE membership.company_id = p_company_id
              AND membership.user_id = v_actor_user_id
              AND membership.deleted_at IS NULL) THEN
        RAISE EXCEPTION 'member invitation capability is required'
            USING ERRCODE = 'PX425';
    END IF;

    PERFORM pg_advisory_xact_lock(
        hashtextextended(
            v_actor_user_id::text || '|' || p_company_id::text || '|' || p_request_id::text,
            0));
    SELECT * INTO v_existing
    FROM foundation.idempotency_records
    WHERE company_id = p_company_id
      AND actor_user_id = v_actor_user_id
      AND command_name = 'foundation.member.invite'
      AND request_id = p_request_id;
    IF FOUND THEN
        RETURN QUERY
        SELECT
            CASE
                WHEN v_existing.payload_hash = p_payload_hash THEN 'replay'
                ELSE 'idempotency_conflict'
            END,
            (v_existing.result_json ->> 'membershipId')::uuid,
            (v_existing.result_json ->> 'userId')::uuid,
            (v_existing.result_json ->> 'employeeId')::uuid,
            (v_existing.result_json ->> 'invitationId')::uuid;
        RETURN;
    END IF;

    INSERT INTO foundation.users (id, display_name, email, created_at)
    VALUES (v_user_id, btrim(p_display_name), lower(btrim(p_email)), v_now);
    INSERT INTO foundation.memberships (
        id, company_id, user_id, created_by_user_id, updated_by_user_id,
        request_id, created_at, updated_at)
    VALUES (
        v_membership_id, p_company_id, v_user_id, v_actor_user_id, v_actor_user_id,
        p_request_id, v_now, v_now);
    INSERT INTO foundation.employees (
        id, company_id, membership_id, display_name, created_by_user_id,
        updated_by_user_id, request_id, created_at, updated_at)
    VALUES (
        v_employee_id, p_company_id, v_membership_id, btrim(p_display_name),
        v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
    INSERT INTO foundation.pending_member_invitations (id, membership_id, user_id, company_id, status)
    VALUES (gen_random_uuid(), v_membership_id, v_user_id, p_company_id, 'pending_delivery')
    RETURNING id INTO v_invitation_id;
    INSERT INTO foundation.idempotency_records (
        id, company_id, actor_user_id, command_name, request_id,
        payload_hash, result_json, created_at)
    VALUES (
        gen_random_uuid(), p_company_id, v_actor_user_id, 'foundation.member.invite',
        p_request_id, p_payload_hash,
        jsonb_build_object(
            'membershipId', v_membership_id,
            'userId', v_user_id,
            'employeeId', v_employee_id,
            'invitationId', v_invitation_id),
        v_now);
    PERFORM audit.append_event(
        v_actor_user_id,
        'company',
        p_company_id,
        'web',
        'foundation.member.invite',
        'member_invitation',
        v_invitation_id,
        'success',
        v_correlation_id,
        NULL,
        '{}'::jsonb);
    RETURN QUERY SELECT 'created'::text, v_membership_id, v_user_id, v_employee_id, v_invitation_id;
END;
$_$;


-- Name: current_actor_is_platform_operator(); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.current_actor_is_platform_operator() RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
    SELECT EXISTS (
        SELECT 1
        FROM foundation.platform_operators AS platform_operator
        WHERE platform_operator.external_identity_id =
            NULLIF(current_setting('app.external_identity_id', true), '')::uuid);
$$;


-- Name: current_actor_memberships(); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.current_actor_memberships() RETURNS TABLE(membership_id uuid, company_id uuid, company_name text)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'foundation', 'pg_catalog'
    AS $$
    SELECT membership.id, company.id, company.name::text
    FROM foundation.memberships AS membership
    INNER JOIN foundation.companies AS company ON company.id = membership.company_id
    WHERE membership.user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
      AND membership.deleted_at IS NULL
      AND company.deleted_at IS NULL
    ORDER BY company.name, company.id;
$$;


-- Name: current_actor_profile(); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.current_actor_profile() RETURNS TABLE(user_id uuid, display_name text)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'foundation', 'pg_catalog'
    AS $$
    SELECT user_record.id, user_record.display_name
    FROM foundation.users AS user_record
    WHERE user_record.id = NULLIF(current_setting('app.user_id', true), '')::uuid;
$$;


-- Name: current_authorized_company_ids(); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.current_authorized_company_ids() RETURNS SETOF uuid
    LANGUAGE sql STABLE
    SET search_path TO 'foundation', 'pg_catalog'
    AS $$
    SELECT value::uuid
    FROM regexp_split_to_table(
        NULLIF(current_setting('app.authorized_company_ids', true), ''), ',') AS value
    WHERE value <> '';
$$;


-- Name: current_company_membership_options(); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.current_company_membership_options() RETURNS TABLE(membership_id uuid, display_name text)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'foundation', 'pg_catalog'
    AS $$
    SELECT membership.id, user_record.display_name::text
    FROM foundation.memberships AS membership
    INNER JOIN foundation.users AS user_record ON user_record.id = membership.user_id
    WHERE membership.company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
      AND membership.deleted_at IS NULL
      AND EXISTS (
          SELECT 1
          FROM foundation.current_actor_memberships() AS actor_membership
          WHERE actor_membership.company_id = membership.company_id)
    ORDER BY user_record.display_name, membership.id;
$$;


-- Name: ensure_company_default_roles(uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.ensure_company_default_roles(p_company_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_seed_actor uuid;
    v_role_id uuid;
    v_grant_count integer;
BEGIN
    IF p_company_id IS NULL THEN
        RAISE EXCEPTION 'company default roles parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    PERFORM set_config('app.company_id', p_company_id::text, true);

    SELECT membership.user_id
    INTO v_seed_actor
    FROM foundation.memberships AS membership
    WHERE membership.company_id = p_company_id
      AND membership.deleted_at IS NULL
    ORDER BY membership.created_at, membership.id
    LIMIT 1;
    IF v_seed_actor IS NULL THEN
        RAISE EXCEPTION 'company has no active membership'
            USING ERRCODE = '23514';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM foundation.roles AS role_record
        WHERE role_record.company_id = p_company_id
          AND role_record.name = 'admin'
          AND role_record.deleted_at IS NULL) THEN
        v_role_id := gen_random_uuid();
        INSERT INTO foundation.roles (
            id, company_id, name, created_by_user_id, updated_by_user_id,
            request_id, created_at, updated_at)
        VALUES (
            v_role_id, p_company_id, 'admin', v_seed_actor, v_seed_actor,
            gen_random_uuid(), clock_timestamp(), clock_timestamp());
        INSERT INTO foundation.role_capabilities (
            id, company_id, role_id, capability_id, scope,
            created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT
            gen_random_uuid(),
            p_company_id,
            v_role_id,
            capability.id,
            'company',
            v_seed_actor,
            v_seed_actor,
            gen_random_uuid(),
            clock_timestamp(),
            clock_timestamp()
        FROM foundation.capabilities AS capability
        WHERE capability.name IN (
            'foundation.roles.assign',
            'foundation.employees.create',
            'foundation.files.upload',
            'foundation.files.confirm')
        ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
            DO NOTHING;
        GET DIAGNOSTICS v_grant_count = ROW_COUNT;
        IF v_grant_count <> 4 THEN
            RAISE EXCEPTION 'admin default role capability catalog is incomplete'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM foundation.roles AS role_record
        WHERE role_record.company_id = p_company_id
          AND role_record.name = 'member'
          AND role_record.deleted_at IS NULL) THEN
        v_role_id := gen_random_uuid();
        INSERT INTO foundation.roles (
            id, company_id, name, created_by_user_id, updated_by_user_id,
            request_id, created_at, updated_at)
        VALUES (
            v_role_id, p_company_id, 'member', v_seed_actor, v_seed_actor,
            gen_random_uuid(), clock_timestamp(), clock_timestamp());
        INSERT INTO foundation.role_capabilities (
            id, company_id, role_id, capability_id, scope,
            created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT
            gen_random_uuid(),
            p_company_id,
            v_role_id,
            capability.id,
            'company',
            v_seed_actor,
            v_seed_actor,
            gen_random_uuid(),
            clock_timestamp(),
            clock_timestamp()
        FROM foundation.capabilities AS capability
        WHERE capability.name IN ('foundation.files.upload')
        ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
            DO NOTHING;
        GET DIAGNOSTICS v_grant_count = ROW_COUNT;
        IF v_grant_count <> 1 THEN
            RAISE EXCEPTION 'member default role capability catalog is incomplete'
                USING ERRCODE = '23514';
        END IF;
    END IF;
END;
$$;


-- Name: ensure_platform_owner_employee_create(uuid, uuid, uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.ensure_platform_owner_employee_create(p_request_id uuid, p_company_id uuid, p_owner_membership_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
    v_has_roles_assign boolean;
BEGIN
    IF p_request_id IS NULL OR p_company_id IS NULL OR p_owner_membership_id IS NULL THEN
        RAISE EXCEPTION 'initial owner employee capability parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    PERFORM set_config('app.company_id', p_company_id::text, true);

    IF v_actor_user_id IS NULL
        OR NOT foundation.current_actor_is_platform_operator() THEN
        RAISE EXCEPTION 'platform operator is required' USING ERRCODE = '42501';
    END IF;
    IF current_setting('app.mfa_assurance', true) IS DISTINCT FROM 'aal2' THEN
        RAISE EXCEPTION 'mfa assurance is required' USING ERRCODE = '42501';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM foundation.organization_provisioning_receipts AS receipt
        WHERE receipt.actor_user_id = v_actor_user_id
          AND receipt.request_id = p_request_id
          AND receipt.company_id = p_company_id
          AND receipt.owner_membership_id = p_owner_membership_id) THEN
        RAISE EXCEPTION 'initial owner provisioning receipt is required'
            USING ERRCODE = '42501';
    END IF;

    SELECT EXISTS (
        SELECT 1
        FROM foundation.memberships AS membership
        INNER JOIN foundation.role_assignments AS assignment
            ON assignment.company_id = membership.company_id
           AND assignment.membership_id = membership.id
           AND assignment.deleted_at IS NULL
        INNER JOIN foundation.roles AS role_record
            ON role_record.company_id = assignment.company_id
           AND role_record.id = assignment.role_id
           AND role_record.deleted_at IS NULL
        INNER JOIN foundation.role_capabilities AS grant_record
            ON grant_record.company_id = assignment.company_id
           AND grant_record.role_id = assignment.role_id
           AND grant_record.scope = 'company'
           AND grant_record.deleted_at IS NULL
        INNER JOIN foundation.capabilities AS capability
            ON capability.id = grant_record.capability_id
           AND capability.name = 'foundation.roles.assign'
        WHERE membership.company_id = p_company_id
          AND membership.id = p_owner_membership_id
          AND membership.deleted_at IS NULL)
    INTO v_has_roles_assign;
    IF NOT v_has_roles_assign THEN
        RAISE EXCEPTION 'initial owner employee capability is unavailable'
            USING ERRCODE = '23514';
    END IF;

    INSERT INTO foundation.role_capabilities (
        id, company_id, role_id, capability_id, scope,
        created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
    SELECT
        gen_random_uuid(),
        assignment.company_id,
        assignment.role_id,
        employee_capability.id,
        'company',
        v_actor_user_id,
        v_actor_user_id,
        gen_random_uuid(),
        clock_timestamp(),
        clock_timestamp()
    FROM foundation.role_assignments AS assignment
    INNER JOIN foundation.role_capabilities AS roles_assign
        ON roles_assign.company_id = assignment.company_id
       AND roles_assign.role_id = assignment.role_id
       AND roles_assign.scope = 'company'
       AND roles_assign.deleted_at IS NULL
    INNER JOIN foundation.capabilities AS assigned_capability
        ON assigned_capability.id = roles_assign.capability_id
       AND assigned_capability.name = 'foundation.roles.assign'
    INNER JOIN foundation.capabilities AS employee_capability
        ON employee_capability.name = 'foundation.employees.create'
    WHERE assignment.company_id = p_company_id
      AND assignment.membership_id = p_owner_membership_id
      AND assignment.deleted_at IS NULL
    ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
        DO NOTHING;
END;
$$;


-- Name: finalize_member_invitation_provider_delivery(uuid, uuid, uuid, text, text, uuid, text); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.finalize_member_invitation_provider_delivery(p_invitation_id uuid, p_attempt_id uuid, p_correlation_id uuid, p_outcome text, p_provider_issuer text, p_provider_subject uuid, p_failure_reason text) RETURNS TABLE(status text, finalized boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_company_id uuid;
    v_membership_id uuid;
    v_membership_company_id uuid;
    v_actor_user_id uuid;
    v_status text;
    v_current_attempt_id uuid;
    v_next_status text;
    v_audit_outcome text;
BEGIN
    IF p_invitation_id IS NULL OR p_attempt_id IS NULL OR p_correlation_id IS NULL
        OR p_outcome NOT IN ('success', 'failed', 'ambiguous') THEN
        RAISE EXCEPTION 'member invitation provider finalization parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT invitation.company_id,
        invitation.membership_id,
        invitation.status,
        invitation.provider_attempt_id
    INTO v_company_id,
        v_membership_id,
        v_status,
        v_current_attempt_id
    FROM foundation.pending_member_invitations AS invitation
    WHERE invitation.id = p_invitation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false;
        RETURN;
    END IF;

    IF v_status IS DISTINCT FROM 'provider_in_flight'
        OR v_current_attempt_id IS DISTINCT FROM p_attempt_id THEN
        RETURN QUERY SELECT v_status, false;
        RETURN;
    END IF;

    SELECT membership.company_id,
        membership.created_by_user_id
    INTO v_membership_company_id,
        v_actor_user_id
    FROM foundation.memberships AS membership
    WHERE membership.id = v_membership_id
      AND membership.deleted_at IS NULL;

    IF NOT FOUND
        OR v_membership_company_id IS DISTINCT FROM v_company_id THEN
        RAISE EXCEPTION 'member invitation membership is inconsistent'
            USING ERRCODE = '23514';
    END IF;

    IF p_outcome = 'success' THEN
        IF p_provider_issuer IS NULL OR btrim(p_provider_issuer) = ''
            OR p_provider_subject IS NULL OR p_failure_reason IS NOT NULL THEN
            RAISE EXCEPTION 'member invitation provider success is invalid'
                USING ERRCODE = '22023';
        END IF;
        v_next_status := 'pending_acceptance';
        v_audit_outcome := 'success';
        UPDATE foundation.pending_member_invitations AS invitation
        SET status = v_next_status,
            provider_completed_at = pg_catalog.clock_timestamp(),
            provider_issuer = btrim(p_provider_issuer),
            provider_subject = p_provider_subject
        WHERE invitation.id = p_invitation_id;
    ELSE
        IF p_provider_issuer IS NOT NULL OR p_provider_subject IS NOT NULL
            OR (p_failure_reason IS DISTINCT FROM CASE p_outcome
                    WHEN 'failed' THEN 'provider_rejected'
                    ELSE 'provider_ambiguous'
                END) THEN
            RAISE EXCEPTION 'member invitation provider failure is invalid'
                USING ERRCODE = '22023';
        END IF;
        v_next_status := CASE p_outcome
            WHEN 'failed' THEN 'provider_failed'
            ELSE 'provider_ambiguous'
        END;
        v_audit_outcome := CASE p_outcome
            WHEN 'failed' THEN 'failure'
            ELSE 'unknown'
        END;
        UPDATE foundation.pending_member_invitations AS invitation
        SET status = v_next_status,
            provider_completed_at = pg_catalog.clock_timestamp(),
            provider_failure_reason = p_failure_reason
        WHERE invitation.id = p_invitation_id;
    END IF;

    PERFORM audit.append_event(
        v_actor_user_id,
        'company',
        v_company_id,
        'worker',
        'member_invitation.delivery.provider',
        'member_invitation',
        p_invitation_id,
        v_audit_outcome,
        p_correlation_id,
        p_failure_reason,
        '{}'::jsonb);
    RETURN QUERY SELECT v_next_status, true;
END;
$$;


-- Name: finalize_owner_invitation_provider_delivery(uuid, uuid, uuid, text, text, uuid, text); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.finalize_owner_invitation_provider_delivery(p_invitation_id uuid, p_attempt_id uuid, p_correlation_id uuid, p_outcome text, p_provider_issuer text, p_provider_subject uuid, p_failure_reason text) RETURNS TABLE(status text, finalized boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_company_id uuid;
    v_invitation_user_id uuid;
    v_receipt_company_id uuid;
    v_receipt_owner_user_id uuid;
    v_actor_user_id uuid;
    v_status text;
    v_current_attempt_id uuid;
    v_next_status text;
    v_audit_outcome text;
BEGIN
    IF p_invitation_id IS NULL OR p_attempt_id IS NULL OR p_correlation_id IS NULL
        OR p_outcome NOT IN ('success', 'failed', 'ambiguous') THEN
        RAISE EXCEPTION 'owner invitation provider finalization parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT invitation.company_id,
        invitation.user_id,
        invitation.status,
        invitation.provider_attempt_id
    INTO v_company_id,
        v_invitation_user_id,
        v_status,
        v_current_attempt_id
    FROM foundation.pending_owner_invitations AS invitation
    WHERE invitation.id = p_invitation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false;
        RETURN;
    END IF;

    IF v_status IS DISTINCT FROM 'provider_in_flight'
        OR v_current_attempt_id IS DISTINCT FROM p_attempt_id THEN
        RETURN QUERY SELECT v_status, false;
        RETURN;
    END IF;

    SELECT receipt.company_id, receipt.owner_user_id, receipt.actor_user_id
    INTO v_receipt_company_id, v_receipt_owner_user_id, v_actor_user_id
    FROM foundation.organization_provisioning_receipts AS receipt
    WHERE receipt.owner_invitation_id = p_invitation_id;

    IF NOT FOUND
        OR v_company_id IS DISTINCT FROM v_receipt_company_id
        OR v_invitation_user_id IS DISTINCT FROM v_receipt_owner_user_id THEN
        RAISE EXCEPTION 'owner invitation provisioning receipt is inconsistent'
            USING ERRCODE = '23514';
    END IF;

    IF p_outcome = 'success' THEN
        IF p_provider_issuer IS NULL OR btrim(p_provider_issuer) = ''
            OR p_provider_subject IS NULL OR p_failure_reason IS NOT NULL THEN
            RAISE EXCEPTION 'owner invitation provider success is invalid'
                USING ERRCODE = '22023';
        END IF;
        v_next_status := 'pending_acceptance';
        v_audit_outcome := 'success';
        UPDATE foundation.pending_owner_invitations AS invitation
        SET status = v_next_status,
            provider_completed_at = pg_catalog.clock_timestamp(),
            provider_issuer = btrim(p_provider_issuer),
            provider_subject = p_provider_subject
        WHERE invitation.id = p_invitation_id;
    ELSE
        IF p_provider_issuer IS NOT NULL OR p_provider_subject IS NOT NULL
            OR (p_failure_reason IS DISTINCT FROM CASE p_outcome
                    WHEN 'failed' THEN 'provider_rejected'
                    ELSE 'provider_ambiguous'
                END) THEN
            RAISE EXCEPTION 'owner invitation provider failure is invalid'
                USING ERRCODE = '22023';
        END IF;
        v_next_status := CASE p_outcome
            WHEN 'failed' THEN 'provider_failed'
            ELSE 'provider_ambiguous'
        END;
        v_audit_outcome := CASE p_outcome
            WHEN 'failed' THEN 'failure'
            ELSE 'unknown'
        END;
        UPDATE foundation.pending_owner_invitations AS invitation
        SET status = v_next_status,
            provider_completed_at = pg_catalog.clock_timestamp(),
            provider_failure_reason = p_failure_reason
        WHERE invitation.id = p_invitation_id;
    END IF;

    PERFORM audit.append_event(
        v_actor_user_id,
        'company',
        v_company_id,
        'worker',
        'owner_invitation.delivery.provider',
        'owner_invitation',
        p_invitation_id,
        v_audit_outcome,
        p_correlation_id,
        p_failure_reason,
        '{}'::jsonb);
    RETURN QUERY SELECT v_next_status, true;
END;
$$;


-- Name: list_company_members_with_roles(uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.list_company_members_with_roles(p_company_id uuid) RETURNS TABLE(membership_id uuid, display_name text, role_ids uuid[], role_names text[], capability_names text[], invitation_status text)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
BEGIN
    IF p_company_id IS NULL THEN
        RAISE EXCEPTION 'member permissions parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    PERFORM set_config('app.company_id', p_company_id::text, true);

    IF v_actor_user_id IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM foundation.memberships AS membership
            INNER JOIN foundation.role_assignments AS assignment
                ON assignment.company_id = membership.company_id
               AND assignment.membership_id = membership.id
               AND assignment.deleted_at IS NULL
            INNER JOIN foundation.roles AS role_record
                ON role_record.company_id = assignment.company_id
               AND role_record.id = assignment.role_id
               AND role_record.deleted_at IS NULL
            INNER JOIN foundation.role_capabilities AS grant_record
                ON grant_record.company_id = assignment.company_id
               AND grant_record.role_id = assignment.role_id
               AND grant_record.scope = 'company'
               AND grant_record.deleted_at IS NULL
            INNER JOIN foundation.capabilities AS capability
                ON capability.id = grant_record.capability_id
               AND capability.name = 'foundation.roles.assign'
            WHERE membership.company_id = p_company_id
              AND membership.user_id = v_actor_user_id
              AND membership.deleted_at IS NULL) THEN
        RAISE EXCEPTION 'member permissions capability is required'
            USING ERRCODE = 'PX425';
    END IF;

    RETURN QUERY
    WITH member_roles AS (
        SELECT
            assignment.membership_id AS member_id,
            role_record.id AS granted_role_id,
            role_record.name AS granted_role_name
        FROM foundation.role_assignments AS assignment
        INNER JOIN foundation.roles AS role_record
            ON role_record.company_id = assignment.company_id
           AND role_record.id = assignment.role_id
           AND role_record.deleted_at IS NULL
        WHERE assignment.company_id = p_company_id
          AND assignment.deleted_at IS NULL
    ),
    roles_aggregated AS (
        SELECT
            member_roles.member_id AS member_id,
            array_agg(member_roles.granted_role_id ORDER BY member_roles.granted_role_name)
                AS aggregated_role_ids,
            array_agg(member_roles.granted_role_name::text ORDER BY member_roles.granted_role_name)
                AS aggregated_role_names
        FROM member_roles
        GROUP BY member_roles.member_id
    ),
    member_capabilities AS (
        SELECT DISTINCT
            member_roles.member_id AS member_id,
            capability.name AS granted_capability_name
        FROM member_roles
        INNER JOIN foundation.role_capabilities AS grant_record
            ON grant_record.company_id = p_company_id
           AND grant_record.role_id = member_roles.granted_role_id
           AND grant_record.scope = 'company'
           AND grant_record.deleted_at IS NULL
        INNER JOIN foundation.capabilities AS capability
            ON capability.id = grant_record.capability_id
    ),
    capabilities_aggregated AS (
        SELECT
            member_capabilities.member_id AS member_id,
            array_agg(
                member_capabilities.granted_capability_name::text
                ORDER BY member_capabilities.granted_capability_name)
                AS aggregated_capability_names
        FROM member_capabilities
        GROUP BY member_capabilities.member_id
    )
    SELECT
        member_row.membership_id,
        member_row.display_name,
        COALESCE(roles_aggregated.aggregated_role_ids, '{}'::uuid[]),
        COALESCE(roles_aggregated.aggregated_role_names, '{}'::text[]),
        COALESCE(capabilities_aggregated.aggregated_capability_names, '{}'::text[]),
        -- varchar(30) precisa do cast: RETURN QUERY exige o tipo exato
        -- da coluna declarada (text) do resultado da função.
        invitation.status::text
    FROM (
        SELECT
            membership.id AS membership_id,
            min(app_user.display_name)::text AS display_name
        FROM foundation.memberships AS membership
        INNER JOIN foundation.users AS app_user
            ON app_user.id = membership.user_id
        WHERE membership.company_id = p_company_id
          AND membership.deleted_at IS NULL
        GROUP BY membership.id
    ) AS member_row
    LEFT JOIN roles_aggregated
        ON roles_aggregated.member_id = member_row.membership_id
    LEFT JOIN capabilities_aggregated
        ON capabilities_aggregated.member_id = member_row.membership_id
    LEFT JOIN foundation.pending_member_invitations AS invitation
        ON invitation.membership_id = member_row.membership_id
    ORDER BY member_row.display_name, member_row.membership_id;
END;
$$;


-- Name: list_company_roles_with_grants(uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.list_company_roles_with_grants(p_company_id uuid) RETURNS TABLE(role_id uuid, name text, capability_names text[])
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
BEGIN
    IF p_company_id IS NULL THEN
        RAISE EXCEPTION 'member permissions parameters are invalid'
            USING ERRCODE = '22023';
    END IF;

    PERFORM set_config('app.company_id', p_company_id::text, true);

    IF v_actor_user_id IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM foundation.memberships AS membership
            INNER JOIN foundation.role_assignments AS assignment
                ON assignment.company_id = membership.company_id
               AND assignment.membership_id = membership.id
               AND assignment.deleted_at IS NULL
            INNER JOIN foundation.roles AS role_record
                ON role_record.company_id = assignment.company_id
               AND role_record.id = assignment.role_id
               AND role_record.deleted_at IS NULL
            INNER JOIN foundation.role_capabilities AS grant_record
                ON grant_record.company_id = assignment.company_id
               AND grant_record.role_id = assignment.role_id
               AND grant_record.scope = 'company'
               AND grant_record.deleted_at IS NULL
            INNER JOIN foundation.capabilities AS capability
                ON capability.id = grant_record.capability_id
               AND capability.name = 'foundation.roles.assign'
            WHERE membership.company_id = p_company_id
              AND membership.user_id = v_actor_user_id
              AND membership.deleted_at IS NULL) THEN
        RAISE EXCEPTION 'member permissions capability is required'
            USING ERRCODE = 'PX425';
    END IF;

    RETURN QUERY
    WITH grants_aggregated AS (
        SELECT
            grant_record.role_id AS granted_role_id,
            array_agg(capability.name::text ORDER BY capability.name)
                AS aggregated_capability_names
        FROM foundation.role_capabilities AS grant_record
        INNER JOIN foundation.capabilities AS capability
            ON capability.id = grant_record.capability_id
        WHERE grant_record.company_id = p_company_id
          AND grant_record.scope = 'company'
          AND grant_record.deleted_at IS NULL
        GROUP BY grant_record.role_id
    )
    SELECT
        role_record.id,
        role_record.name::text,
        COALESCE(grants_aggregated.aggregated_capability_names, '{}'::text[])
    FROM foundation.roles AS role_record
    LEFT JOIN grants_aggregated
        ON grants_aggregated.granted_role_id = role_record.id
    WHERE role_record.company_id = p_company_id
      AND role_record.deleted_at IS NULL
    ORDER BY role_record.name, role_record.id;
END;
$$;


-- Name: lock_user_for_session_admission(uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.lock_user_for_session_admission(p_user_id uuid) RETURNS TABLE(sessions_valid_after timestamp with time zone)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'foundation', 'pg_catalog'
    AS $$
BEGIN
    RETURN QUERY
    SELECT user_record.sessions_valid_after
    FROM foundation.users AS user_record
    WHERE user_record.id = p_user_id
    FOR UPDATE;
END;
$$;


-- Name: provision_platform_organization(uuid, text, text, text, text, text); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.provision_platform_organization(p_request_id uuid, p_group_name text, p_company_name text, p_owner_display_name text, p_owner_email text, p_payload_hash text) RETURNS TABLE(status text, group_id uuid, company_id uuid, owner_user_id uuid, owner_membership_id uuid, owner_invitation_id uuid)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $_$
DECLARE
    v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
    v_correlation_id uuid := COALESCE(NULLIF(current_setting('app.correlation_id', true), '')::uuid, gen_random_uuid());
    v_group_id uuid := gen_random_uuid();
    v_company_id uuid := gen_random_uuid();
    v_owner_user_id uuid := gen_random_uuid();
    v_membership_id uuid := gen_random_uuid();
    v_role_id uuid := gen_random_uuid();
    v_invitation_id uuid := gen_random_uuid();
    v_existing foundation.organization_provisioning_receipts%ROWTYPE;
    v_capability_count integer;
    v_now timestamp with time zone := clock_timestamp();
BEGIN
    IF v_actor_user_id IS NULL OR NOT foundation.current_actor_is_platform_operator() THEN
        RAISE EXCEPTION 'platform operator is required' USING ERRCODE = '42501';
    END IF;
    IF current_setting('app.mfa_assurance', true) IS DISTINCT FROM 'aal2' THEN
        RAISE EXCEPTION 'mfa assurance is required' USING ERRCODE = '42501';
    END IF;
    IF p_request_id IS NULL OR p_payload_hash !~ '^[0-9a-f]{64}$'
        OR p_group_name IS NULL OR btrim(p_group_name) = '' OR char_length(btrim(p_group_name)) > 200
        OR p_company_name IS NULL OR btrim(p_company_name) = '' OR char_length(btrim(p_company_name)) > 200
        OR p_owner_display_name IS NULL OR btrim(p_owner_display_name) = '' OR char_length(btrim(p_owner_display_name)) > 200
        OR p_owner_email IS NULL OR btrim(p_owner_email) = '' OR char_length(btrim(p_owner_email)) > 320 THEN
        RAISE EXCEPTION 'organization provisioning parameters are invalid' USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(v_actor_user_id::text || '|' || p_request_id::text, 0));
    SELECT * INTO v_existing FROM foundation.organization_provisioning_receipts
    WHERE actor_user_id = v_actor_user_id AND request_id = p_request_id;
    IF FOUND THEN
        IF v_existing.payload_hash = p_payload_hash THEN
            RETURN QUERY SELECT 'replay'::text, v_existing.group_id, v_existing.company_id, v_existing.owner_user_id, v_existing.owner_membership_id, v_existing.owner_invitation_id;
        ELSE
            RETURN QUERY SELECT 'idempotency_conflict'::text, v_existing.group_id, v_existing.company_id, v_existing.owner_user_id, v_existing.owner_membership_id, v_existing.owner_invitation_id;
        END IF;
        RETURN;
    END IF;

    INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
    VALUES (v_group_id, btrim(p_group_name), v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
    INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
    VALUES (v_company_id, v_group_id, btrim(p_company_name), v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
    INSERT INTO foundation.users (id, display_name, email, created_at)
    VALUES (v_owner_user_id, btrim(p_owner_display_name), lower(btrim(p_owner_email)), v_now);
    INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
    VALUES (v_membership_id, v_company_id, v_owner_user_id, v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
    INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
    VALUES (v_role_id, v_company_id, 'owner', v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
    INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
    SELECT gen_random_uuid(), v_company_id, v_role_id, capability.id, 'company', v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now
    FROM foundation.capabilities AS capability
    WHERE capability.name IN ('foundation.roles.assign', 'foundation.memberships.revoke', 'foundation.files.upload', 'foundation.files.confirm');
    GET DIAGNOSTICS v_capability_count = ROW_COUNT;
    IF v_capability_count <> 4 THEN
        RAISE EXCEPTION 'owner capability catalog is incomplete' USING ERRCODE = '23514';
    END IF;
    INSERT INTO foundation.role_assignments (id, company_id, role_id, membership_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
    VALUES (gen_random_uuid(), v_company_id, v_role_id, v_membership_id, v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
    INSERT INTO foundation.pending_owner_invitations (id, user_id, company_id, status)
    VALUES (v_invitation_id, v_owner_user_id, v_company_id, 'pending_delivery');
    INSERT INTO foundation.organization_provisioning_receipts (id, actor_user_id, request_id, payload_hash, group_id, company_id, owner_user_id, owner_membership_id, owner_invitation_id)
    VALUES (gen_random_uuid(), v_actor_user_id, p_request_id, p_payload_hash, v_group_id, v_company_id, v_owner_user_id, v_membership_id, v_invitation_id);
    PERFORM audit.append_event(v_actor_user_id, 'global', NULL, 'web', 'platform.organization.provision', 'organization', v_company_id, 'success', v_correlation_id, NULL, '{}'::jsonb);
    RETURN QUERY SELECT 'created'::text, v_group_id, v_company_id, v_owner_user_id, v_membership_id, v_invitation_id;
END;
$_$;


-- Name: reprovision_external_identity(uuid, uuid, text, text, text, uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.reprovision_external_identity(p_user_id uuid, p_expected_identity_id uuid, p_expected_issuer text, p_expected_old_subject text, p_new_subject text, p_correlation_id uuid) RETURNS TABLE(status text)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'foundation', 'pg_temp'
    AS $$
DECLARE
    v_deployment_actor_id constant uuid := '00000000-0000-0000-0000-000000000001';
    v_identity_user_id uuid;
    v_identity_issuer text;
    v_identity_subject text;
    v_existing_new_identity_id uuid;
    v_now timestamp with time zone;
BEGIN
    IF p_user_id IS NULL
        OR p_expected_identity_id IS NULL
        OR p_expected_issuer IS NULL
        OR btrim(p_expected_issuer) = ''
        OR char_length(p_expected_issuer) > 200
        OR p_expected_old_subject IS NULL
        OR btrim(p_expected_old_subject) = ''
        OR char_length(p_expected_old_subject) > 400
        OR p_new_subject IS NULL
        OR btrim(p_new_subject) = ''
        OR char_length(p_new_subject) > 400
        OR p_correlation_id IS NULL THEN
        RETURN QUERY SELECT 'invalid'::text;
        RETURN;
    END IF;

    PERFORM 1 FROM foundation.users AS user_record
    WHERE user_record.id = p_user_id FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'invalid'::text;
        RETURN;
    END IF;

    SELECT identity_record.user_id, identity_record.issuer, identity_record.subject
    INTO v_identity_user_id, v_identity_issuer, v_identity_subject
    FROM foundation.external_identities AS identity_record
    WHERE identity_record.id = p_expected_identity_id FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'invalid'::text;
        RETURN;
    END IF;

    IF v_identity_user_id = p_user_id
        AND v_identity_issuer = p_expected_issuer
        AND v_identity_subject = p_new_subject THEN
        RETURN QUERY SELECT 'already_configured'::text;
        RETURN;
    END IF;

    IF v_identity_user_id <> p_user_id
        OR v_identity_issuer <> p_expected_issuer
        OR v_identity_subject <> p_expected_old_subject THEN
        RETURN QUERY SELECT 'conflict'::text;
        RETURN;
    END IF;

    SELECT identity_record.id INTO v_existing_new_identity_id
    FROM foundation.external_identities AS identity_record
    WHERE identity_record.issuer = p_expected_issuer
      AND identity_record.subject = p_new_subject FOR UPDATE;
    IF FOUND AND v_existing_new_identity_id <> p_expected_identity_id THEN
        RETURN QUERY SELECT 'conflict'::text;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    UPDATE foundation.external_identities SET subject = p_new_subject
    WHERE id = p_expected_identity_id;
    UPDATE foundation.users
    SET sessions_valid_after = CASE
        WHEN sessions_valid_after IS NULL OR sessions_valid_after < v_now THEN v_now
        ELSE sessions_valid_after
    END
    WHERE id = p_user_id;
    UPDATE foundation.user_sessions
    SET revoked_at = v_now,
        revoked_by_user_id = p_user_id,
        revocation_reason = 'identity_reprovisioned'
    WHERE user_id = p_user_id AND revoked_at IS NULL;

    PERFORM audit.append_event(
        v_deployment_actor_id, 'global', NULL, 'worker', 'identity.external.reprovision',
        'user', p_user_id, 'success', p_correlation_id,
        'same_person_recovery', '{}'::jsonb);
    RETURN QUERY SELECT 'reprovisioned'::text;
END;
$$;


-- Name: revoke_all_user_sessions(uuid); Type: FUNCTION; Schema: foundation; Owner: -

CREATE FUNCTION foundation.revoke_all_user_sessions(p_user_id uuid) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'foundation', 'pg_catalog'
    AS $$
DECLARE
    v_now timestamp with time zone;
    v_revoked_session_count integer;
BEGIN
    PERFORM 1
    FROM foundation.users AS user_record
    WHERE user_record.id = p_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'internal user does not exist'
            USING ERRCODE = 'P0002';
    END IF;

    v_now := clock_timestamp();

    WITH revoked_sessions AS (
        UPDATE foundation.user_sessions
        SET revoked_at = v_now,
            revoked_by_user_id = p_user_id,
            revocation_reason = 'self_revoke_all'
        WHERE user_id = p_user_id
          AND revoked_at IS NULL
        RETURNING 1)
    SELECT count(*)::integer
    INTO v_revoked_session_count
    FROM revoked_sessions;

    IF v_revoked_session_count > 0 THEN
        UPDATE foundation.users
        SET sessions_valid_after = CASE
            WHEN sessions_valid_after IS NULL OR sessions_valid_after < v_now THEN v_now
            ELSE sessions_valid_after
        END
        WHERE id = p_user_id;
    END IF;

    RETURN v_revoked_session_count;
END;
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

-- Name: audit_events; Type: TABLE; Schema: audit; Owner: -

CREATE TABLE audit.audit_events (
    id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    requested_company_id uuid,
    source character varying(20) NOT NULL,
    action character varying(200) NOT NULL,
    resource_type character varying(200) NOT NULL,
    resource_id uuid,
    outcome character varying(20) NOT NULL,
    correlation_id uuid NOT NULL,
    reason character varying(1000),
    metadata_json jsonb NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    scope character varying(20) NOT NULL,
    CONSTRAINT ck_audit_events_requested_company CHECK (((((scope)::text = 'global'::text) AND (requested_company_id IS NULL)) OR (((scope)::text = 'company'::text) AND (requested_company_id IS NOT NULL))))
);


-- Name: agent_execution_ledger; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.agent_execution_ledger (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    channel character varying(50) NOT NULL,
    inbound_message_id uuid,
    request_id uuid NOT NULL,
    journey character varying(200) NOT NULL,
    payload_hash character varying(64) NOT NULL,
    state character varying(30) NOT NULL,
    safe_result_json jsonb,
    reason_code character varying(100),
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    row_version bigint DEFAULT 1 NOT NULL,
    CONSTRAINT ck_agent_execution_ledger_channel_inbound_message CHECK (((((channel)::text = 'web'::text) AND (inbound_message_id IS NULL)) OR (((channel)::text = ANY ((ARRAY['whatsapp'::character varying, 'telegram'::character varying])::text[])) AND (inbound_message_id IS NOT NULL)))),
    CONSTRAINT ck_agent_execution_ledger_payload_hash_format CHECK (((payload_hash)::text ~ '^[0-9a-f]{64}$'::text)),
    CONSTRAINT ck_agent_execution_ledger_state CHECK (((state)::text = ANY ((ARRAY['claimed'::character varying, 'vision_in_flight'::character varying, 'model_in_flight'::character varying, 'completed'::character varying, 'failed_closed'::character varying, 'ambiguous'::character varying])::text[]))),
    CONSTRAINT ck_agent_execution_ledger_terminal_data CHECK (((((state)::text = 'completed'::text) AND (safe_result_json IS NOT NULL) AND (reason_code IS NULL)) OR (((state)::text = ANY ((ARRAY['failed_closed'::character varying, 'ambiguous'::character varying])::text[])) AND (safe_result_json IS NULL) AND (reason_code IS NOT NULL)) OR (((state)::text = ANY ((ARRAY['claimed'::character varying, 'vision_in_flight'::character varying, 'model_in_flight'::character varying])::text[])) AND (safe_result_json IS NULL) AND (reason_code IS NULL))))
);

ALTER TABLE ONLY foundation.agent_execution_ledger FORCE ROW LEVEL SECURITY;


-- Name: approvals; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.approvals (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    action character varying(200) NOT NULL,
    payload_hash character varying(64) NOT NULL,
    policy_version character varying(50) NOT NULL,
    proposal_id uuid NOT NULL,
    confirmation_request_id uuid,
    expires_at timestamp with time zone NOT NULL,
    consumed_at timestamp with time zone,
    result_json jsonb,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL,
    resource_key character varying(200) DEFAULT ''::character varying NOT NULL,
    CONSTRAINT ck_approvals_consumption_complete CHECK ((((consumed_at IS NULL) AND (confirmation_request_id IS NULL) AND (result_json IS NULL)) OR ((consumed_at IS NOT NULL) AND (confirmation_request_id IS NOT NULL) AND (result_json IS NOT NULL)))),
    CONSTRAINT ck_approvals_expires_after_creation CHECK ((expires_at > created_at)),
    CONSTRAINT ck_approvals_payload_hash_format CHECK (((payload_hash)::text ~ '^[0-9a-f]{64}$'::text)),
    CONSTRAINT ck_approvals_terminal_exclusive CHECK ((NOT ((consumed_at IS NOT NULL) AND (deleted_at IS NOT NULL)))),
    CONSTRAINT ck_approvals_veto_complete CHECK ((((deleted_at IS NULL) AND (deleted_by_user_id IS NULL) AND (deleted_reason IS NULL)) OR ((deleted_at IS NOT NULL) AND (deleted_by_user_id IS NOT NULL) AND ((deleted_reason)::text = ANY ((ARRAY['approval_vetoed'::character varying, 'approval_expired'::character varying])::text[])))))
);

ALTER TABLE ONLY foundation.approvals FORCE ROW LEVEL SECURITY;


-- Name: capabilities; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.capabilities (
    id uuid NOT NULL,
    name character varying(200) NOT NULL,
    description character varying(1000),
    created_at timestamp with time zone NOT NULL
);


-- Name: companies; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.companies (
    id uuid NOT NULL,
    group_id uuid NOT NULL,
    name character varying(200) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.companies FORCE ROW LEVEL SECURITY;


-- Name: employees; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.employees (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    membership_id uuid NOT NULL,
    display_name character varying(200) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.employees FORCE ROW LEVEL SECURITY;


-- Name: external_identities; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.external_identities (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    issuer character varying(200) NOT NULL,
    subject character varying(400) NOT NULL,
    created_at timestamp with time zone NOT NULL
);


-- Name: files; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.files (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    uploader_membership_id uuid NOT NULL,
    classification character varying(100) NOT NULL,
    checksum character varying(128) NOT NULL,
    provider_key character varying(1000) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL,
    extension character varying(20) NOT NULL,
    failure_reason character varying(200),
    mime_type character varying(100) NOT NULL,
    origin character varying(100) NOT NULL,
    original_file_name character varying(255) NOT NULL,
    processing_state character varying(30) NOT NULL,
    retention_until timestamp with time zone NOT NULL,
    size_bytes bigint NOT NULL,
    scan_engine character varying(100),
    scan_engine_version character varying(50),
    scan_signature_version character varying(50),
    scanned_at_utc timestamp with time zone
);

ALTER TABLE ONLY foundation.files FORCE ROW LEVEL SECURITY;


-- Name: groups; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.groups (
    id uuid NOT NULL,
    name character varying(200) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.groups FORCE ROW LEVEL SECURITY;


-- Name: idempotency_records; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.idempotency_records (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    command_name character varying(200) NOT NULL,
    request_id uuid NOT NULL,
    payload_hash character varying(128) NOT NULL,
    result_json jsonb NOT NULL,
    created_at timestamp with time zone NOT NULL
);

ALTER TABLE ONLY foundation.idempotency_records FORCE ROW LEVEL SECURITY;


-- Name: memberships; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.memberships (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    user_id uuid NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.memberships FORCE ROW LEVEL SECURITY;


-- Name: organization_provisioning_receipts; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.organization_provisioning_receipts (
    id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    payload_hash character varying(64) NOT NULL,
    group_id uuid NOT NULL,
    company_id uuid NOT NULL,
    owner_user_id uuid NOT NULL,
    owner_membership_id uuid NOT NULL,
    owner_invitation_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT organization_provisioning_receipts_payload_hash_check CHECK (((payload_hash)::text ~ '^[0-9a-f]{64}$'::text))
);

ALTER TABLE ONLY foundation.organization_provisioning_receipts FORCE ROW LEVEL SECURITY;


-- Name: pending_member_invitations; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.pending_member_invitations (
    id uuid NOT NULL,
    membership_id uuid NOT NULL,
    user_id uuid NOT NULL,
    company_id uuid NOT NULL,
    status character varying(30) NOT NULL,
    created_at timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    provider_pending_at timestamp with time zone,
    provider_attempt_id uuid,
    provider_started_at timestamp with time zone,
    provider_completed_at timestamp with time zone,
    provider_issuer text,
    provider_subject uuid,
    provider_failure_reason character varying(50),
    CONSTRAINT ck_pending_member_invitations_provider_subject_not_empty CHECK (((provider_subject IS NULL) OR (provider_subject <> '00000000-0000-0000-0000-000000000000'::uuid))),
    CONSTRAINT ck_pending_member_invitations_status CHECK (((((status)::text = 'pending_delivery'::text) AND (provider_pending_at IS NULL) AND (provider_attempt_id IS NULL) AND (provider_started_at IS NULL) AND (provider_completed_at IS NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'pending_provider'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NULL) AND (provider_started_at IS NULL) AND (provider_completed_at IS NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'provider_in_flight'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'pending_acceptance'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NOT NULL) AND (provider_issuer IS NOT NULL) AND (provider_subject IS NOT NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'provider_failed'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NOT NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND ((provider_failure_reason)::text = 'provider_rejected'::text)) OR (((status)::text = 'provider_ambiguous'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NOT NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND ((provider_failure_reason)::text = 'provider_ambiguous'::text))))
);

ALTER TABLE ONLY foundation.pending_member_invitations FORCE ROW LEVEL SECURITY;


-- Name: pending_owner_invitations; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.pending_owner_invitations (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    company_id uuid NOT NULL,
    status character varying(30) NOT NULL,
    created_at timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    provider_pending_at timestamp with time zone,
    provider_attempt_id uuid,
    provider_started_at timestamp with time zone,
    provider_completed_at timestamp with time zone,
    provider_issuer text,
    provider_subject uuid,
    provider_failure_reason character varying(50),
    CONSTRAINT ck_pending_owner_invitations_provider_subject_not_empty CHECK (((provider_subject IS NULL) OR (provider_subject <> '00000000-0000-0000-0000-000000000000'::uuid))),
    CONSTRAINT ck_pending_owner_invitations_status CHECK (((((status)::text = 'pending_delivery'::text) AND (provider_pending_at IS NULL) AND (provider_attempt_id IS NULL) AND (provider_started_at IS NULL) AND (provider_completed_at IS NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'pending_provider'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NULL) AND (provider_started_at IS NULL) AND (provider_completed_at IS NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'provider_in_flight'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'pending_acceptance'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NOT NULL) AND (provider_issuer IS NOT NULL) AND (provider_subject IS NOT NULL) AND (provider_failure_reason IS NULL)) OR (((status)::text = 'provider_failed'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NOT NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND ((provider_failure_reason)::text = 'provider_rejected'::text)) OR (((status)::text = 'provider_ambiguous'::text) AND (provider_pending_at IS NOT NULL) AND (provider_attempt_id IS NOT NULL) AND (provider_started_at IS NOT NULL) AND (provider_completed_at IS NOT NULL) AND (provider_issuer IS NULL) AND (provider_subject IS NULL) AND ((provider_failure_reason)::text = 'provider_ambiguous'::text))))
);

ALTER TABLE ONLY foundation.pending_owner_invitations FORCE ROW LEVEL SECURITY;


-- Name: platform_operators; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.platform_operators (
    singleton smallint NOT NULL,
    external_identity_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    CONSTRAINT ck_platform_operators_singleton CHECK ((singleton = 1))
);

ALTER TABLE ONLY foundation.platform_operators FORCE ROW LEVEL SECURITY;


-- Name: role_assignments; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.role_assignments (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    role_id uuid NOT NULL,
    membership_id uuid NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.role_assignments FORCE ROW LEVEL SECURITY;


-- Name: role_capabilities; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.role_capabilities (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    role_id uuid NOT NULL,
    capability_id uuid NOT NULL,
    scope character varying(20) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL,
    CONSTRAINT ck_role_capabilities_scope CHECK (((scope)::text = ANY ((ARRAY['self'::character varying, 'team'::character varying, 'unit'::character varying, 'company'::character varying, 'group'::character varying])::text[])))
);

ALTER TABLE ONLY foundation.role_capabilities FORCE ROW LEVEL SECURITY;


-- Name: roles; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.roles (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    name character varying(200) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.roles FORCE ROW LEVEL SECURITY;


-- Name: teams; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.teams (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    unit_id uuid NOT NULL,
    name character varying(200) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.teams FORCE ROW LEVEL SECURITY;


-- Name: units; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.units (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    name character varying(200) NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NOT NULL,
    request_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    deleted_at timestamp with time zone,
    deleted_by_user_id uuid,
    deleted_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL
);

ALTER TABLE ONLY foundation.units FORCE ROW LEVEL SECURITY;


-- Name: user_sessions; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.user_sessions (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    issuer character varying(200) NOT NULL,
    provider_session_id uuid NOT NULL,
    provider_authenticated_at timestamp with time zone NOT NULL,
    first_seen_at timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    token_expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    revoked_by_user_id uuid,
    revocation_reason character varying(500),
    row_version bigint DEFAULT 1 NOT NULL,
    CONSTRAINT ck_user_sessions_authenticated_before_expiration CHECK ((provider_authenticated_at < token_expires_at)),
    CONSTRAINT ck_user_sessions_revocation_complete CHECK ((((revoked_at IS NULL) AND (revoked_by_user_id IS NULL) AND (revocation_reason IS NULL)) OR ((revoked_at IS NOT NULL) AND (revoked_by_user_id IS NOT NULL) AND (revocation_reason IS NOT NULL) AND (btrim((revocation_reason)::text) <> ''::text))))
);


-- Name: users; Type: TABLE; Schema: foundation; Owner: -

CREATE TABLE foundation.users (
    id uuid NOT NULL,
    display_name character varying(200) NOT NULL,
    email character varying(320) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    row_version bigint DEFAULT 1 NOT NULL,
    sessions_valid_after timestamp with time zone
);


-- Name: audit_events pk_audit_events; Type: CONSTRAINT; Schema: audit; Owner: -

ALTER TABLE ONLY audit.audit_events
    ADD CONSTRAINT pk_audit_events PRIMARY KEY (id);


-- Name: agent_execution_ledger ak_agent_execution_ledger_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.agent_execution_ledger
    ADD CONSTRAINT ak_agent_execution_ledger_company_id_id UNIQUE (company_id, id);


-- Name: approvals ak_approvals_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.approvals
    ADD CONSTRAINT ak_approvals_company_id_id UNIQUE (company_id, id);


-- Name: employees ak_employees_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.employees
    ADD CONSTRAINT ak_employees_company_id_id UNIQUE (company_id, id);


-- Name: files ak_files_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.files
    ADD CONSTRAINT ak_files_company_id_id UNIQUE (company_id, id);


-- Name: idempotency_records ak_idempotency_records_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.idempotency_records
    ADD CONSTRAINT ak_idempotency_records_company_id_id UNIQUE (company_id, id);


-- Name: memberships ak_memberships_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.memberships
    ADD CONSTRAINT ak_memberships_company_id_id UNIQUE (company_id, id);


-- Name: role_assignments ak_role_assignments_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_assignments
    ADD CONSTRAINT ak_role_assignments_company_id_id UNIQUE (company_id, id);


-- Name: role_capabilities ak_role_capabilities_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_capabilities
    ADD CONSTRAINT ak_role_capabilities_company_id_id UNIQUE (company_id, id);


-- Name: roles ak_roles_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.roles
    ADD CONSTRAINT ak_roles_company_id_id UNIQUE (company_id, id);


-- Name: teams ak_teams_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.teams
    ADD CONSTRAINT ak_teams_company_id_id UNIQUE (company_id, id);


-- Name: units ak_units_company_id_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.units
    ADD CONSTRAINT ak_units_company_id_id UNIQUE (company_id, id);


-- Name: organization_provisioning_receipts organization_provisioning_receipts_pkey; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT organization_provisioning_receipts_pkey PRIMARY KEY (id);


-- Name: pending_member_invitations pending_member_invitations_pkey; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_member_invitations
    ADD CONSTRAINT pending_member_invitations_pkey PRIMARY KEY (id);


-- Name: pending_owner_invitations pending_owner_invitations_pkey; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_owner_invitations
    ADD CONSTRAINT pending_owner_invitations_pkey PRIMARY KEY (id);


-- Name: agent_execution_ledger pk_agent_execution_ledger; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.agent_execution_ledger
    ADD CONSTRAINT pk_agent_execution_ledger PRIMARY KEY (id);


-- Name: approvals pk_approvals; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.approvals
    ADD CONSTRAINT pk_approvals PRIMARY KEY (id);


-- Name: capabilities pk_capabilities; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.capabilities
    ADD CONSTRAINT pk_capabilities PRIMARY KEY (id);


-- Name: companies pk_companies; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.companies
    ADD CONSTRAINT pk_companies PRIMARY KEY (id);


-- Name: employees pk_employees; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.employees
    ADD CONSTRAINT pk_employees PRIMARY KEY (id);


-- Name: external_identities pk_external_identities; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.external_identities
    ADD CONSTRAINT pk_external_identities PRIMARY KEY (id);


-- Name: files pk_files; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.files
    ADD CONSTRAINT pk_files PRIMARY KEY (id);


-- Name: groups pk_groups; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.groups
    ADD CONSTRAINT pk_groups PRIMARY KEY (id);


-- Name: idempotency_records pk_idempotency_records; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.idempotency_records
    ADD CONSTRAINT pk_idempotency_records PRIMARY KEY (id);


-- Name: memberships pk_memberships; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.memberships
    ADD CONSTRAINT pk_memberships PRIMARY KEY (id);


-- Name: platform_operators pk_platform_operators; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.platform_operators
    ADD CONSTRAINT pk_platform_operators PRIMARY KEY (singleton);


-- Name: role_assignments pk_role_assignments; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_assignments
    ADD CONSTRAINT pk_role_assignments PRIMARY KEY (id);


-- Name: role_capabilities pk_role_capabilities; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_capabilities
    ADD CONSTRAINT pk_role_capabilities PRIMARY KEY (id);


-- Name: roles pk_roles; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.roles
    ADD CONSTRAINT pk_roles PRIMARY KEY (id);


-- Name: teams pk_teams; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.teams
    ADD CONSTRAINT pk_teams PRIMARY KEY (id);


-- Name: units pk_units; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.units
    ADD CONSTRAINT pk_units PRIMARY KEY (id);


-- Name: user_sessions pk_user_sessions; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.user_sessions
    ADD CONSTRAINT pk_user_sessions PRIMARY KEY (id);


-- Name: users pk_users; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.users
    ADD CONSTRAINT pk_users PRIMARY KEY (id);


-- Name: organization_provisioning_receipts uq_organization_provisioning_receipts_actor_request; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT uq_organization_provisioning_receipts_actor_request UNIQUE (actor_user_id, request_id);


-- Name: organization_provisioning_receipts uq_organization_provisioning_receipts_owner_invitation_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT uq_organization_provisioning_receipts_owner_invitation_id UNIQUE (owner_invitation_id);


-- Name: pending_member_invitations uq_pending_member_invitations_membership_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_member_invitations
    ADD CONSTRAINT uq_pending_member_invitations_membership_id UNIQUE (membership_id);


-- Name: pending_owner_invitations uq_pending_owner_invitations_user_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_owner_invitations
    ADD CONSTRAINT uq_pending_owner_invitations_user_id UNIQUE (user_id);


-- Name: platform_operators uq_platform_operators_external_identity_id; Type: CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.platform_operators
    ADD CONSTRAINT uq_platform_operators_external_identity_id UNIQUE (external_identity_id);


-- Name: ix_audit_events_actor_user_id_occurred_at; Type: INDEX; Schema: audit; Owner: -

CREATE INDEX ix_audit_events_actor_user_id_occurred_at ON audit.audit_events USING btree (actor_user_id, occurred_at);


-- Name: ix_audit_events_requested_company_id_occurred_at; Type: INDEX; Schema: audit; Owner: -

CREATE INDEX ix_audit_events_requested_company_id_occurred_at ON audit.audit_events USING btree (requested_company_id, occurred_at);


-- Name: ix_agent_execution_ledger_company_id_actor_user_id_channel_requ; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_agent_execution_ledger_company_id_actor_user_id_channel_requ ON foundation.agent_execution_ledger USING btree (company_id, actor_user_id, channel, request_id);


-- Name: ix_approvals_actor_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_approvals_actor_user_id ON foundation.approvals USING btree (actor_user_id);


-- Name: ix_approvals_company_id_actor_user_id_resource_key; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_approvals_company_id_actor_user_id_resource_key ON foundation.approvals USING btree (company_id, actor_user_id, resource_key) WHERE ((consumed_at IS NULL) AND (deleted_at IS NULL));


-- Name: ix_approvals_confirmation_request_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_approvals_confirmation_request_id ON foundation.approvals USING btree (confirmation_request_id) WHERE (confirmation_request_id IS NOT NULL);


-- Name: ix_approvals_proposal_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_approvals_proposal_id ON foundation.approvals USING btree (proposal_id);


-- Name: ix_capabilities_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_capabilities_name ON foundation.capabilities USING btree (name);


-- Name: ix_companies_group_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_companies_group_id ON foundation.companies USING btree (group_id);


-- Name: ix_companies_normalized_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_companies_normalized_name ON foundation.companies USING btree (lower(btrim((name)::text))) WHERE (deleted_at IS NULL);


-- Name: ix_employees_company_id_membership_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_employees_company_id_membership_id ON foundation.employees USING btree (company_id, membership_id) WHERE (deleted_at IS NULL);


-- Name: ix_external_identities_issuer_subject; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_external_identities_issuer_subject ON foundation.external_identities USING btree (issuer, subject);


-- Name: ix_external_identities_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_external_identities_user_id ON foundation.external_identities USING btree (user_id);


-- Name: ix_files_company_id_provider_key; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_files_company_id_provider_key ON foundation.files USING btree (company_id, provider_key) WHERE (deleted_at IS NULL);


-- Name: ix_files_company_id_uploader_membership_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_files_company_id_uploader_membership_id ON foundation.files USING btree (company_id, uploader_membership_id);


-- Name: ix_groups_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_groups_name ON foundation.groups USING btree (lower(btrim((name)::text))) WHERE (deleted_at IS NULL);


-- Name: ix_idempotency_records_company_id_actor_user_id_command_name_r; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_idempotency_records_company_id_actor_user_id_command_name_r ON foundation.idempotency_records USING btree (company_id, actor_user_id, command_name, request_id);


-- Name: ix_memberships_company_id_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_memberships_company_id_user_id ON foundation.memberships USING btree (company_id, user_id) WHERE (deleted_at IS NULL);


-- Name: ix_memberships_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_memberships_user_id ON foundation.memberships USING btree (user_id);


-- Name: ix_pending_member_invitations_provider_backlog; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_pending_member_invitations_provider_backlog ON foundation.pending_member_invitations USING btree (provider_pending_at, id) WHERE ((status)::text = 'pending_provider'::text);


-- Name: ix_pending_owner_invitations_provider_backlog; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_pending_owner_invitations_provider_backlog ON foundation.pending_owner_invitations USING btree (provider_pending_at, id) WHERE ((status)::text = 'pending_provider'::text);


-- Name: ix_role_assignments_company_id_membership_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_role_assignments_company_id_membership_id ON foundation.role_assignments USING btree (company_id, membership_id);


-- Name: ix_role_assignments_company_id_role_id_membership_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_role_assignments_company_id_role_id_membership_id ON foundation.role_assignments USING btree (company_id, role_id, membership_id) WHERE (deleted_at IS NULL);


-- Name: ix_role_capabilities_capability_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_role_capabilities_capability_id ON foundation.role_capabilities USING btree (capability_id);


-- Name: ix_role_capabilities_company_id_role_id_capability_id_scope; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_role_capabilities_company_id_role_id_capability_id_scope ON foundation.role_capabilities USING btree (company_id, role_id, capability_id, scope) WHERE (deleted_at IS NULL);


-- Name: ix_roles_company_id_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_roles_company_id_name ON foundation.roles USING btree (company_id, name) WHERE (deleted_at IS NULL);


-- Name: ix_teams_company_id_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_teams_company_id_name ON foundation.teams USING btree (company_id, name) WHERE (deleted_at IS NULL);


-- Name: ix_teams_company_id_unit_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_teams_company_id_unit_id ON foundation.teams USING btree (company_id, unit_id);


-- Name: ix_units_company_id_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_units_company_id_name ON foundation.units USING btree (company_id, name) WHERE (deleted_at IS NULL);


-- Name: ix_user_sessions_issuer_provider_session_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_user_sessions_issuer_provider_session_id ON foundation.user_sessions USING btree (issuer, provider_session_id);


-- Name: ix_user_sessions_revoked_by_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_user_sessions_revoked_by_user_id ON foundation.user_sessions USING btree (revoked_by_user_id);


-- Name: ix_user_sessions_user_id_revoked_at; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_user_sessions_user_id_revoked_at ON foundation.user_sessions USING btree (user_id, revoked_at);


-- Name: ix_users_email; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_users_email ON foundation.users USING btree (lower(btrim((email)::text)));


-- Name: audit_events audit_events_reject_mutation; Type: TRIGGER; Schema: audit; Owner: -

CREATE TRIGGER audit_events_reject_mutation BEFORE DELETE OR UPDATE ON audit.audit_events FOR EACH ROW EXECUTE FUNCTION audit.reject_audit_event_mutation();


-- Name: companies trg_seed_company_lookup_defaults; Type: TRIGGER; Schema: foundation; Owner: -

CREATE TRIGGER trg_seed_company_lookup_defaults AFTER INSERT ON foundation.companies FOR EACH ROW EXECUTE FUNCTION service_desk.seed_company_lookup_defaults_trigger();


-- Name: agent_execution_ledger fk_agent_execution_ledger_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.agent_execution_ledger
    ADD CONSTRAINT fk_agent_execution_ledger_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: approvals fk_approvals_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.approvals
    ADD CONSTRAINT fk_approvals_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: approvals fk_approvals_users_actor_user_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.approvals
    ADD CONSTRAINT fk_approvals_users_actor_user_id FOREIGN KEY (actor_user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: companies fk_companies_groups_group_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.companies
    ADD CONSTRAINT fk_companies_groups_group_id FOREIGN KEY (group_id) REFERENCES foundation.groups(id) ON DELETE RESTRICT;


-- Name: employees fk_employees_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.employees
    ADD CONSTRAINT fk_employees_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: employees fk_employees_memberships_company_id_membership_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.employees
    ADD CONSTRAINT fk_employees_memberships_company_id_membership_id FOREIGN KEY (company_id, membership_id) REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT;


-- Name: external_identities fk_external_identities_users_user_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.external_identities
    ADD CONSTRAINT fk_external_identities_users_user_id FOREIGN KEY (user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: files fk_files_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.files
    ADD CONSTRAINT fk_files_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: files fk_files_memberships_company_id_uploader_membership_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.files
    ADD CONSTRAINT fk_files_memberships_company_id_uploader_membership_id FOREIGN KEY (company_id, uploader_membership_id) REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT;


-- Name: idempotency_records fk_idempotency_records_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.idempotency_records
    ADD CONSTRAINT fk_idempotency_records_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: memberships fk_memberships_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.memberships
    ADD CONSTRAINT fk_memberships_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: memberships fk_memberships_users_user_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.memberships
    ADD CONSTRAINT fk_memberships_users_user_id FOREIGN KEY (user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: platform_operators fk_platform_operators_external_identities_external_identity_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.platform_operators
    ADD CONSTRAINT fk_platform_operators_external_identities_external_identity_id FOREIGN KEY (external_identity_id) REFERENCES foundation.external_identities(id) ON DELETE RESTRICT;


-- Name: role_assignments fk_role_assignments_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_assignments
    ADD CONSTRAINT fk_role_assignments_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: role_assignments fk_role_assignments_memberships_company_id_membership_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_assignments
    ADD CONSTRAINT fk_role_assignments_memberships_company_id_membership_id FOREIGN KEY (company_id, membership_id) REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT;


-- Name: role_assignments fk_role_assignments_roles_company_id_role_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_assignments
    ADD CONSTRAINT fk_role_assignments_roles_company_id_role_id FOREIGN KEY (company_id, role_id) REFERENCES foundation.roles(company_id, id) ON DELETE RESTRICT;


-- Name: role_capabilities fk_role_capabilities_capabilities_capability_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_capabilities
    ADD CONSTRAINT fk_role_capabilities_capabilities_capability_id FOREIGN KEY (capability_id) REFERENCES foundation.capabilities(id) ON DELETE RESTRICT;


-- Name: role_capabilities fk_role_capabilities_roles_company_id_role_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.role_capabilities
    ADD CONSTRAINT fk_role_capabilities_roles_company_id_role_id FOREIGN KEY (company_id, role_id) REFERENCES foundation.roles(company_id, id) ON DELETE RESTRICT;


-- Name: roles fk_roles_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.roles
    ADD CONSTRAINT fk_roles_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: teams fk_teams_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.teams
    ADD CONSTRAINT fk_teams_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: teams fk_teams_units_company_id_unit_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.teams
    ADD CONSTRAINT fk_teams_units_company_id_unit_id FOREIGN KEY (company_id, unit_id) REFERENCES foundation.units(company_id, id) ON DELETE RESTRICT;


-- Name: units fk_units_companies_company_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.units
    ADD CONSTRAINT fk_units_companies_company_id FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: user_sessions fk_user_sessions_users_revoked_by_user_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.user_sessions
    ADD CONSTRAINT fk_user_sessions_users_revoked_by_user_id FOREIGN KEY (revoked_by_user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: user_sessions fk_user_sessions_users_user_id; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.user_sessions
    ADD CONSTRAINT fk_user_sessions_users_user_id FOREIGN KEY (user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: organization_provisioning_receipts organization_provisioning_receipts_actor_user_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT organization_provisioning_receipts_actor_user_id_fkey FOREIGN KEY (actor_user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: organization_provisioning_receipts organization_provisioning_receipts_company_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT organization_provisioning_receipts_company_id_fkey FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: organization_provisioning_receipts organization_provisioning_receipts_group_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT organization_provisioning_receipts_group_id_fkey FOREIGN KEY (group_id) REFERENCES foundation.groups(id) ON DELETE RESTRICT;


-- Name: organization_provisioning_receipts organization_provisioning_receipts_owner_invitation_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT organization_provisioning_receipts_owner_invitation_id_fkey FOREIGN KEY (owner_invitation_id) REFERENCES foundation.pending_owner_invitations(id) ON DELETE RESTRICT;


-- Name: organization_provisioning_receipts organization_provisioning_receipts_owner_membership_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT organization_provisioning_receipts_owner_membership_id_fkey FOREIGN KEY (owner_membership_id) REFERENCES foundation.memberships(id) ON DELETE RESTRICT;


-- Name: organization_provisioning_receipts organization_provisioning_receipts_owner_user_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.organization_provisioning_receipts
    ADD CONSTRAINT organization_provisioning_receipts_owner_user_id_fkey FOREIGN KEY (owner_user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: pending_member_invitations pending_member_invitations_company_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_member_invitations
    ADD CONSTRAINT pending_member_invitations_company_id_fkey FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: pending_member_invitations pending_member_invitations_membership_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_member_invitations
    ADD CONSTRAINT pending_member_invitations_membership_id_fkey FOREIGN KEY (membership_id) REFERENCES foundation.memberships(id) ON DELETE RESTRICT;


-- Name: pending_member_invitations pending_member_invitations_user_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_member_invitations
    ADD CONSTRAINT pending_member_invitations_user_id_fkey FOREIGN KEY (user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: pending_owner_invitations pending_owner_invitations_company_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_owner_invitations
    ADD CONSTRAINT pending_owner_invitations_company_id_fkey FOREIGN KEY (company_id) REFERENCES foundation.companies(id) ON DELETE RESTRICT;


-- Name: pending_owner_invitations pending_owner_invitations_user_id_fkey; Type: FK CONSTRAINT; Schema: foundation; Owner: -

ALTER TABLE ONLY foundation.pending_owner_invitations
    ADD CONSTRAINT pending_owner_invitations_user_id_fkey FOREIGN KEY (user_id) REFERENCES foundation.users(id) ON DELETE RESTRICT;


-- Name: agent_execution_ledger; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.agent_execution_ledger ENABLE ROW LEVEL SECURITY;

-- Name: agent_execution_ledger agent_execution_ledger_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY agent_execution_ledger_company_isolation ON foundation.agent_execution_ledger USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) AND (actor_user_id = (NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid))) WITH CHECK (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) AND (actor_user_id = (NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid)));


-- Name: approvals; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.approvals ENABLE ROW LEVEL SECURITY;

-- Name: approvals approvals_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY approvals_company_isolation ON foundation.approvals USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) AND ((actor_user_id = (NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid) OR ((resource_key)::text <> ''::text)))) WITH CHECK (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) AND ((actor_user_id = (NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid) OR ((resource_key)::text <> ''::text))));


-- Name: capabilities; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.capabilities ENABLE ROW LEVEL SECURITY;

-- Name: capabilities capabilities_demandas_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_demandas_owner_read ON foundation.capabilities FOR SELECT TO vinx_provisioning_owner USING (((name)::text = ANY ((ARRAY['demandas.demand.read'::character varying, 'demandas.demand.create'::character varying, 'demandas.demand.advance'::character varying, 'demandas.demand.assign'::character varying, 'demandas.demand.participants.manage'::character varying, 'demandas.routine.manage'::character varying])::text[])));


-- Name: capabilities capabilities_employee_directory_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_employee_directory_owner_select ON foundation.capabilities FOR SELECT TO vinx_employee_directory_owner USING (((name)::text = ANY ((ARRAY['foundation.roles.assign'::character varying, 'foundation.employees.create'::character varying])::text[])));


-- Name: capabilities capabilities_notifications_owner_paguai_approve_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_notifications_owner_paguai_approve_read ON foundation.capabilities FOR SELECT TO vinx_notifications_owner USING (((name)::text = 'paguai.draft.approve'::text));


-- Name: capabilities capabilities_notifications_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_notifications_owner_read ON foundation.capabilities FOR SELECT TO vinx_notifications_owner USING (((name)::text = ANY ((ARRAY['foundation.files.upload'::character varying, 'foundation.files.read-ocr'::character varying, 'ai.read'::character varying, 'paguai.draft.create'::character varying])::text[])));


-- Name: capabilities capabilities_permissions_owner_granted_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_permissions_owner_granted_select ON foundation.capabilities FOR SELECT TO vinx_permissions_owner USING ((EXISTS ( SELECT 1
   FROM foundation.role_capabilities grant_record
  WHERE ((grant_record.capability_id = capabilities.id) AND (grant_record.deleted_at IS NULL)))));


-- Name: capabilities capabilities_permissions_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_permissions_owner_select ON foundation.capabilities FOR SELECT TO vinx_permissions_owner USING (((name)::text = ANY ((ARRAY['foundation.roles.assign'::character varying, 'foundation.employees.create'::character varying, 'foundation.files.upload'::character varying, 'foundation.files.confirm'::character varying])::text[])));


-- Name: capabilities capabilities_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_provisioning_owner ON foundation.capabilities FOR SELECT TO vinx_provisioning_owner USING (((name)::text = ANY ((ARRAY['foundation.roles.assign'::character varying, 'foundation.memberships.revoke'::character varying, 'foundation.files.upload'::character varying, 'foundation.files.confirm'::character varying])::text[])));


-- Name: capabilities capabilities_runtime_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_runtime_read ON foundation.capabilities FOR SELECT TO vinx_runtime USING (true);


-- Name: capabilities capabilities_worker_agent_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY capabilities_worker_agent_read ON foundation.capabilities FOR SELECT TO vinx_worker USING (((name)::text = ANY ((ARRAY['foundation.files.upload'::character varying, 'foundation.files.read-ocr'::character varying, 'ai.read'::character varying, 'paguai.draft.create'::character varying])::text[])));


-- Name: companies; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.companies ENABLE ROW LEVEL SECURITY;

-- Name: companies companies_authorization_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_authorization_owner_read ON foundation.companies FOR SELECT TO vinx_authorization_owner USING (true);


-- Name: companies companies_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_authorized_company_read ON foundation.companies FOR SELECT USING (((id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: companies companies_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_company_isolation ON foundation.companies USING ((id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: companies companies_notifications_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_notifications_owner_read ON foundation.companies FOR SELECT TO vinx_notifications_owner USING (true);


-- Name: companies companies_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_provisioning_owner ON foundation.companies FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: employees; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.employees ENABLE ROW LEVEL SECURITY;

-- Name: employees employees_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY employees_authorized_company_read ON foundation.employees FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: employees employees_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY employees_company_isolation ON foundation.employees USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: employees employees_employee_directory_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY employees_employee_directory_owner_insert ON foundation.employees FOR INSERT TO vinx_employee_directory_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: employees employees_permissions_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY employees_permissions_owner_insert ON foundation.employees FOR INSERT TO vinx_permissions_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: files; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.files ENABLE ROW LEVEL SECURITY;

-- Name: files files_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY files_authorized_company_read ON foundation.files FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: files files_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY files_company_isolation ON foundation.files USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: groups; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.groups ENABLE ROW LEVEL SECURITY;

-- Name: groups groups_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY groups_authorized_company_read ON foundation.groups FOR SELECT USING ((id IN ( SELECT company.group_id
   FROM foundation.companies company
  WHERE ((company.id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company.id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))))));


-- Name: groups groups_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY groups_company_isolation ON foundation.groups USING ((id = ( SELECT company.group_id
   FROM foundation.companies company
  WHERE (company.id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)))) WITH CHECK ((id = ( SELECT company.group_id
   FROM foundation.companies company
  WHERE (company.id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid))));


-- Name: groups groups_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY groups_provisioning_owner ON foundation.groups FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: idempotency_records idempotency_employee_directory_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY idempotency_employee_directory_owner_insert ON foundation.idempotency_records FOR INSERT TO vinx_employee_directory_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: idempotency_records idempotency_employee_directory_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY idempotency_employee_directory_owner_select ON foundation.idempotency_records FOR SELECT TO vinx_employee_directory_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: idempotency_records idempotency_permissions_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY idempotency_permissions_owner_insert ON foundation.idempotency_records FOR INSERT TO vinx_permissions_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: idempotency_records idempotency_permissions_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY idempotency_permissions_owner_select ON foundation.idempotency_records FOR SELECT TO vinx_permissions_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: idempotency_records; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.idempotency_records ENABLE ROW LEVEL SECURITY;

-- Name: idempotency_records idempotency_records_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY idempotency_records_authorized_company_read ON foundation.idempotency_records FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: idempotency_records idempotency_records_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY idempotency_records_company_isolation ON foundation.idempotency_records USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: memberships; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.memberships ENABLE ROW LEVEL SECURITY;

-- Name: memberships memberships_authorization_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_authorization_owner_read ON foundation.memberships FOR SELECT TO vinx_authorization_owner USING (true);


-- Name: memberships memberships_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_authorized_company_read ON foundation.memberships FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: memberships memberships_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_company_isolation ON foundation.memberships USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: memberships memberships_demandas_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_demandas_owner_read ON foundation.memberships FOR SELECT TO vinx_demandas_owner USING (true);


-- Name: memberships memberships_employee_directory_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_employee_directory_owner_insert ON foundation.memberships FOR INSERT TO vinx_employee_directory_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: memberships memberships_employee_directory_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_employee_directory_owner_select ON foundation.memberships FOR SELECT TO vinx_employee_directory_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: memberships memberships_notifications_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_notifications_owner_read ON foundation.memberships FOR SELECT TO vinx_notifications_owner USING (true);


-- Name: memberships memberships_permissions_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_permissions_owner_insert ON foundation.memberships FOR INSERT TO vinx_permissions_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: memberships memberships_permissions_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_permissions_owner_select ON foundation.memberships FOR SELECT TO vinx_permissions_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: memberships memberships_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_provisioning_owner ON foundation.memberships FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: memberships memberships_worker_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY memberships_worker_owner_read ON foundation.memberships FOR SELECT TO vinx_worker_owner USING ((deleted_at IS NULL));


-- Name: organization_provisioning_receipts; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.organization_provisioning_receipts ENABLE ROW LEVEL SECURITY;

-- Name: organization_provisioning_receipts organization_provisioning_receipts_employee_directory_owner_sel; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY organization_provisioning_receipts_employee_directory_owner_sel ON foundation.organization_provisioning_receipts FOR SELECT TO vinx_employee_directory_owner USING (((actor_user_id = (NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid) AND (company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)));


-- Name: organization_provisioning_receipts organization_provisioning_receipts_provisioning_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY organization_provisioning_receipts_provisioning_owner_insert ON foundation.organization_provisioning_receipts FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: organization_provisioning_receipts organization_provisioning_receipts_provisioning_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY organization_provisioning_receipts_provisioning_owner_select ON foundation.organization_provisioning_receipts FOR SELECT TO vinx_provisioning_owner USING (true);


-- Name: organization_provisioning_receipts organization_provisioning_receipts_worker_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY organization_provisioning_receipts_worker_owner_read ON foundation.organization_provisioning_receipts FOR SELECT TO vinx_worker_owner USING (true);


-- Name: pending_member_invitations; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.pending_member_invitations ENABLE ROW LEVEL SECURITY;

-- Name: pending_member_invitations pending_member_invitations_permissions_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY pending_member_invitations_permissions_owner_insert ON foundation.pending_member_invitations FOR INSERT TO vinx_permissions_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: pending_member_invitations pending_member_invitations_permissions_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY pending_member_invitations_permissions_owner_select ON foundation.pending_member_invitations FOR SELECT TO vinx_permissions_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: pending_member_invitations pending_member_invitations_worker_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY pending_member_invitations_worker_owner_read ON foundation.pending_member_invitations FOR SELECT TO vinx_worker_owner USING (true);


-- Name: pending_member_invitations pending_member_invitations_worker_owner_update; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY pending_member_invitations_worker_owner_update ON foundation.pending_member_invitations FOR UPDATE TO vinx_worker_owner USING (true) WITH CHECK (true);


-- Name: pending_owner_invitations; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.pending_owner_invitations ENABLE ROW LEVEL SECURITY;

-- Name: pending_owner_invitations pending_owner_invitations_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY pending_owner_invitations_provisioning_owner ON foundation.pending_owner_invitations FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: pending_owner_invitations pending_owner_invitations_worker_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY pending_owner_invitations_worker_owner_read ON foundation.pending_owner_invitations FOR SELECT TO vinx_worker_owner USING (true);


-- Name: pending_owner_invitations pending_owner_invitations_worker_owner_update; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY pending_owner_invitations_worker_owner_update ON foundation.pending_owner_invitations FOR UPDATE TO vinx_worker_owner USING (true) WITH CHECK (true);


-- Name: platform_operators; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.platform_operators ENABLE ROW LEVEL SECURITY;

-- Name: platform_operators platform_operators_owner_access; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY platform_operators_owner_access ON foundation.platform_operators TO vinx_platform_owner USING (true) WITH CHECK (true);


-- Name: role_assignments; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.role_assignments ENABLE ROW LEVEL SECURITY;

-- Name: role_assignments role_assignments_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_authorized_company_read ON foundation.role_assignments FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: role_assignments role_assignments_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_company_isolation ON foundation.role_assignments USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_assignments role_assignments_employee_directory_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_employee_directory_owner_select ON foundation.role_assignments FOR SELECT TO vinx_employee_directory_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_assignments role_assignments_notifications_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_notifications_owner_read ON foundation.role_assignments FOR SELECT TO vinx_notifications_owner USING (true);


-- Name: role_assignments role_assignments_permissions_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_permissions_owner_select ON foundation.role_assignments FOR SELECT TO vinx_permissions_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_assignments role_assignments_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_provisioning_owner ON foundation.role_assignments FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: role_capabilities; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.role_capabilities ENABLE ROW LEVEL SECURITY;

-- Name: role_capabilities role_capabilities_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_authorized_company_read ON foundation.role_capabilities FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: role_capabilities role_capabilities_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_company_isolation ON foundation.role_capabilities USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_capabilities role_capabilities_demandas_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_demandas_owner_read ON foundation.role_capabilities FOR SELECT TO vinx_provisioning_owner USING ((capability_id = ANY (ARRAY['6ff8b25a-1c37-58cc-894b-97221467c3d4'::uuid, 'd3c10860-a26c-5eac-b684-8e2faac9ad99'::uuid, '74403a29-bdae-55a9-b43b-774eca3f03b2'::uuid, '00b09e31-8693-5b62-b59a-cad5994f3378'::uuid, 'd207f7a5-7fdb-5a7d-a87c-2b4210f36712'::uuid, 'ec28a347-1ade-5586-bf33-e6f1cbd59620'::uuid])));


-- Name: role_capabilities role_capabilities_employee_directory_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_employee_directory_owner_insert ON foundation.role_capabilities FOR INSERT TO vinx_employee_directory_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_capabilities role_capabilities_employee_directory_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_employee_directory_owner_select ON foundation.role_capabilities FOR SELECT TO vinx_employee_directory_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_capabilities role_capabilities_notifications_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_notifications_owner_read ON foundation.role_capabilities FOR SELECT TO vinx_notifications_owner USING (true);


-- Name: role_capabilities role_capabilities_permissions_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_permissions_owner_insert ON foundation.role_capabilities FOR INSERT TO vinx_permissions_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_capabilities role_capabilities_permissions_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_permissions_owner_select ON foundation.role_capabilities FOR SELECT TO vinx_permissions_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_capabilities role_capabilities_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_provisioning_owner ON foundation.role_capabilities FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: roles; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.roles ENABLE ROW LEVEL SECURITY;

-- Name: roles roles_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_authorized_company_read ON foundation.roles FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: roles roles_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_company_isolation ON foundation.roles USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: roles roles_employee_directory_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_employee_directory_owner_select ON foundation.roles FOR SELECT TO vinx_employee_directory_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: roles roles_notifications_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_notifications_owner_read ON foundation.roles FOR SELECT TO vinx_notifications_owner USING (true);


-- Name: roles roles_permissions_owner_insert; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_permissions_owner_insert ON foundation.roles FOR INSERT TO vinx_permissions_owner WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: roles roles_permissions_owner_select; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_permissions_owner_select ON foundation.roles FOR SELECT TO vinx_permissions_owner USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: roles roles_provisioning_owner; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_provisioning_owner ON foundation.roles FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);


-- Name: teams; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.teams ENABLE ROW LEVEL SECURITY;

-- Name: teams teams_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY teams_authorized_company_read ON foundation.teams FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: teams teams_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY teams_company_isolation ON foundation.teams USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: units; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.units ENABLE ROW LEVEL SECURITY;

-- Name: units units_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY units_authorized_company_read ON foundation.units FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: units units_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY units_company_isolation ON foundation.units USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- PostgreSQL database dump complete
