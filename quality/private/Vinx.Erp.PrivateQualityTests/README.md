# Private quality suite

This project is owned by the quality verifier. It intentionally contains no
product scenarios yet: the first Foundation slice adds verifier-authored abuse
coverage for forged or missing company context and cross-company leakage.

Run it only through `scripts/verify-private.sh` in the separate verifier
execution context. Builders must not add, inspect, or execute private tests.
