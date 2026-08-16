REVOKE ALL ON SCHEMA vinx_wolverine, vinx_wolverine_queues FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA vinx_wolverine, vinx_wolverine_queues FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA vinx_wolverine, vinx_wolverine_queues FROM PUBLIC;

REVOKE ALL ON SCHEMA vinx_wolverine, vinx_wolverine_queues FROM vinx_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA vinx_wolverine, vinx_wolverine_queues FROM vinx_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA vinx_wolverine, vinx_wolverine_queues FROM vinx_runtime;
GRANT USAGE ON SCHEMA vinx_wolverine, vinx_wolverine_queues TO vinx_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA vinx_wolverine, vinx_wolverine_queues TO vinx_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA vinx_wolverine, vinx_wolverine_queues TO vinx_runtime;

ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vinx_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_queues
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vinx_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine
    GRANT USAGE, SELECT ON SEQUENCES TO vinx_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_queues
    GRANT USAGE, SELECT ON SEQUENCES TO vinx_runtime;
