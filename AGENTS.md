# Vinx ERP agent rules

- Read the architecture design and module docs before behavior changes.
- Do not read, list or execute quality/private; a separate verifier owns it.
- Do not write directly to main; use a codex/* branch and pull request.
- Run the smallest public test, then the complete public verification command.
- Do not add an abstraction, adapter, skill or dependency without a present use case and documented reason.
- Authorization, explicit company context, audit, observability and documentation are definition-of-done requirements.
- Never put secrets, tokens, medical content, prompt bodies or file content in logs, fixtures or Markdown.
