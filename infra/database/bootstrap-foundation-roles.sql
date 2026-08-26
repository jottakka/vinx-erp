DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_migrator') THEN
        ALTER ROLE vinx_migrator LOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_migrator LOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_runtime') THEN
        ALTER ROLE vinx_runtime LOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_runtime LOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_worker') THEN
        ALTER ROLE vinx_worker LOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_worker LOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_worker_owner') THEN
        ALTER ROLE vinx_worker_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_worker_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_audit_owner') THEN
        ALTER ROLE vinx_audit_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_audit_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_identity_owner') THEN
        ALTER ROLE vinx_identity_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_identity_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_authorization_owner') THEN
        ALTER ROLE vinx_authorization_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_authorization_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_platform_owner') THEN
        ALTER ROLE vinx_platform_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_platform_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_provisioning_owner') THEN
        ALTER ROLE vinx_provisioning_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_provisioning_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_employee_directory_owner') THEN
        ALTER ROLE vinx_employee_directory_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_employee_directory_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_notifications_owner') THEN
        ALTER ROLE vinx_notifications_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_notifications_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vinx_permissions_owner') THEN
        ALTER ROLE vinx_permissions_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    ELSE
        CREATE ROLE vinx_permissions_owner NOLOGIN NOINHERIT NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;
END
$$;

DO $$
DECLARE
    role_name name;
BEGIN
    FOR role_name IN
        SELECT role.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE member.rolname = 'vinx_worker'
    LOOP
        EXECUTE format('REVOKE %I FROM vinx_worker', role_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_worker'
    LOOP
        EXECUTE format('REVOKE vinx_worker FROM %I', member_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_worker_owner'
          AND member.rolname <> 'vinx_migrator'
    LOOP
        EXECUTE format('REVOKE vinx_worker_owner FROM %I', member_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_provisioning_owner'
          AND member.rolname <> 'vinx_migrator'
    LOOP
        EXECUTE format('REVOKE vinx_provisioning_owner FROM %I', member_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_authorization_owner'
          AND member.rolname <> 'vinx_migrator'
    LOOP
        EXECUTE format('REVOKE vinx_authorization_owner FROM %I', member_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_platform_owner'
          AND member.rolname <> 'vinx_migrator'
    LOOP
        EXECUTE format('REVOKE vinx_platform_owner FROM %I', member_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_employee_directory_owner'
          AND member.rolname <> 'vinx_migrator'
    LOOP
        EXECUTE format('REVOKE vinx_employee_directory_owner FROM %I', member_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_notifications_owner'
          AND member.rolname <> 'vinx_migrator'
    LOOP
        EXECUTE format('REVOKE vinx_notifications_owner FROM %I', member_name);
    END LOOP;
END
$$;

DO $$
DECLARE
    member_name name;
BEGIN
    FOR member_name IN
        SELECT member.rolname
        FROM pg_auth_members AS membership
        INNER JOIN pg_roles AS role ON role.oid = membership.roleid
        INNER JOIN pg_roles AS member ON member.oid = membership.member
        WHERE role.rolname = 'vinx_permissions_owner'
          AND member.rolname <> 'vinx_migrator'
    LOOP
        EXECUTE format('REVOKE vinx_permissions_owner FROM %I', member_name);
    END LOOP;
END
$$;

GRANT vinx_audit_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_identity_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_authorization_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_platform_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_provisioning_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_employee_directory_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_worker_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_notifications_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;
GRANT vinx_permissions_owner TO vinx_migrator WITH INHERIT FALSE, SET TRUE;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO vinx_migrator;
REVOKE ALL ON SCHEMA public FROM vinx_runtime, vinx_worker, vinx_worker_owner, vinx_audit_owner, vinx_identity_owner,
    vinx_authorization_owner, vinx_platform_owner, vinx_provisioning_owner, vinx_employee_directory_owner,
    vinx_notifications_owner, vinx_permissions_owner;

DO $$
DECLARE
    database_name text := current_database();
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO vinx_migrator, vinx_runtime, vinx_worker', database_name);
    EXECUTE format('GRANT CREATE ON DATABASE %I TO vinx_migrator', database_name);
    EXECUTE format(
        'REVOKE CREATE ON DATABASE %I FROM vinx_runtime, vinx_worker, vinx_worker_owner, vinx_audit_owner, vinx_identity_owner, vinx_authorization_owner, vinx_platform_owner, vinx_provisioning_owner, vinx_employee_directory_owner, vinx_notifications_owner, vinx_permissions_owner',
        database_name);
END
$$;
