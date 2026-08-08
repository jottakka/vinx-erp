# OpenAPI contract policy

HTTP contracts are published from `ApiHost` and documented per module. The OpenAPI document describes externally observable API behavior; it does not replace authorization or database protections.

Every command accepts an explicit company context and is authorized server side using membership, named capability and scope. Responses must not reveal data from a company the actor cannot access. The frontend may use effective capabilities to hide inaccessible UI, but a forged request receives denial and an auditable outcome.

Version a contract when a consumer-visible breaking change is necessary. For the internal pilot, prefer additive fields and migration windows over parallel API generations. Document the capability, audit action, idempotency behavior and redaction rules beside high-risk operations.

Binary file bytes do not travel through the normal API request path. The API authorizes a private, short-lived signed upload or download URL and records file metadata and audit evidence.
