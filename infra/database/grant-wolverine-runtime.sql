REVOKE ALL ON SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_worker
    REVOKE ALL ON TABLES FROM vinx_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_worker
    REVOKE ALL ON SEQUENCES FROM vinx_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine
    REVOKE ALL ON TABLES FROM vinx_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine
    REVOKE ALL ON SEQUENCES FROM vinx_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_queues
    REVOKE ALL ON TABLES FROM vinx_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_queues
    REVOKE ALL ON SEQUENCES FROM vinx_worker;

REVOKE ALL ON SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM vinx_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM vinx_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM vinx_runtime;
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

REVOKE ALL ON SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM vinx_worker;
REVOKE ALL ON ALL TABLES IN SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM vinx_worker;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA vinx_wolverine, vinx_wolverine_worker, vinx_wolverine_queues FROM vinx_worker;

GRANT USAGE ON SCHEMA vinx_wolverine_worker, vinx_wolverine_queues TO vinx_worker;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA vinx_wolverine_worker TO vinx_worker;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA vinx_wolverine_worker TO vinx_worker;
GRANT SELECT, INSERT, UPDATE, DELETE
    ON TABLE vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery,
        vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery_scheduled,
        vinx_wolverine_queues.wolverine_queue_member_invitation_delivery,
        vinx_wolverine_queues.wolverine_queue_member_invitation_delivery_scheduled,
        vinx_wolverine_queues.wolverine_queue_notification_delivery,
        vinx_wolverine_queues.wolverine_queue_notification_delivery_scheduled,
        vinx_wolverine_queues.wolverine_queue_whatsapp_inbound,
        vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_scheduled,
        vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_documents,
        vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_documents_scheduled,
        vinx_wolverine_queues.wolverine_queue_telegram_inbound,
        vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled
    TO vinx_worker;

ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_worker
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vinx_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_worker
    GRANT USAGE, SELECT ON SEQUENCES TO vinx_worker;
