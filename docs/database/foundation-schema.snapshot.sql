


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
    SET search_path TO 'audit', 'pg_catalog'
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
    size_bytes bigint NOT NULL
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


-- Name: ix_audit_events_actor_user_id_occurred_at; Type: INDEX; Schema: audit; Owner: -

CREATE INDEX ix_audit_events_actor_user_id_occurred_at ON audit.audit_events USING btree (actor_user_id, occurred_at);


-- Name: ix_audit_events_requested_company_id_occurred_at; Type: INDEX; Schema: audit; Owner: -

CREATE INDEX ix_audit_events_requested_company_id_occurred_at ON audit.audit_events USING btree (requested_company_id, occurred_at);


-- Name: ix_approvals_actor_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_approvals_actor_user_id ON foundation.approvals USING btree (actor_user_id);


-- Name: ix_approvals_company_id_actor_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_approvals_company_id_actor_user_id ON foundation.approvals USING btree (company_id, actor_user_id) WHERE ((consumed_at IS NULL) AND (deleted_at IS NULL));


-- Name: ix_approvals_confirmation_request_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_approvals_confirmation_request_id ON foundation.approvals USING btree (confirmation_request_id) WHERE (confirmation_request_id IS NOT NULL);


-- Name: ix_approvals_proposal_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_approvals_proposal_id ON foundation.approvals USING btree (proposal_id);


-- Name: ix_capabilities_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_capabilities_name ON foundation.capabilities USING btree (name);


-- Name: ix_companies_group_id_name; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_companies_group_id_name ON foundation.companies USING btree (group_id, name) WHERE (deleted_at IS NULL);


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

CREATE UNIQUE INDEX ix_groups_name ON foundation.groups USING btree (name) WHERE (deleted_at IS NULL);


-- Name: ix_idempotency_records_company_id_actor_user_id_command_name_r; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_idempotency_records_company_id_actor_user_id_command_name_r ON foundation.idempotency_records USING btree (company_id, actor_user_id, command_name, request_id);


-- Name: ix_memberships_company_id_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE UNIQUE INDEX ix_memberships_company_id_user_id ON foundation.memberships USING btree (company_id, user_id) WHERE (deleted_at IS NULL);


-- Name: ix_memberships_user_id; Type: INDEX; Schema: foundation; Owner: -

CREATE INDEX ix_memberships_user_id ON foundation.memberships USING btree (user_id);


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

CREATE UNIQUE INDEX ix_users_email ON foundation.users USING btree (email);


-- Name: audit_events audit_events_reject_mutation; Type: TRIGGER; Schema: audit; Owner: -

CREATE TRIGGER audit_events_reject_mutation BEFORE DELETE OR UPDATE ON audit.audit_events FOR EACH ROW EXECUTE FUNCTION audit.reject_audit_event_mutation();


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


-- Name: approvals; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.approvals ENABLE ROW LEVEL SECURITY;

-- Name: approvals approvals_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY approvals_company_isolation ON foundation.approvals USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) AND (actor_user_id = (NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid))) WITH CHECK (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) AND (actor_user_id = (NULLIF(current_setting('app.user_id'::text, true), ''::text))::uuid)));


-- Name: companies; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.companies ENABLE ROW LEVEL SECURITY;

-- Name: companies companies_authorization_owner_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_authorization_owner_read ON foundation.companies FOR SELECT TO vinx_authorization_owner USING (true);


-- Name: companies companies_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_authorized_company_read ON foundation.companies FOR SELECT USING (((id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: companies companies_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY companies_company_isolation ON foundation.companies USING ((id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: employees; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.employees ENABLE ROW LEVEL SECURITY;

-- Name: employees employees_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY employees_authorized_company_read ON foundation.employees FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: employees employees_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY employees_company_isolation ON foundation.employees USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


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


-- Name: role_assignments; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.role_assignments ENABLE ROW LEVEL SECURITY;

-- Name: role_assignments role_assignments_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_authorized_company_read ON foundation.role_assignments FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: role_assignments role_assignments_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_assignments_company_isolation ON foundation.role_assignments USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: role_capabilities; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.role_capabilities ENABLE ROW LEVEL SECURITY;

-- Name: role_capabilities role_capabilities_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_authorized_company_read ON foundation.role_capabilities FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: role_capabilities role_capabilities_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY role_capabilities_company_isolation ON foundation.role_capabilities USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


-- Name: roles; Type: ROW SECURITY; Schema: foundation; Owner: -

ALTER TABLE foundation.roles ENABLE ROW LEVEL SECURITY;

-- Name: roles roles_authorized_company_read; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_authorized_company_read ON foundation.roles FOR SELECT USING (((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid) OR (company_id IN ( SELECT foundation.current_authorized_company_ids() AS current_authorized_company_ids))));


-- Name: roles roles_company_isolation; Type: POLICY; Schema: foundation; Owner: -

CREATE POLICY roles_company_isolation ON foundation.roles USING ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid)) WITH CHECK ((company_id = (NULLIF(current_setting('app.company_id'::text, true), ''::text))::uuid));


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
