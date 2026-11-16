# Incident Response

1. Contain: stop promotion (freeze routing table / release tag), keep evidence.
2. Roll back via statepoints and `rollback-model` or revert the release; never force-push `main`.
3. Classify with the threat model; if security, switch to `handle-security-report`.
4. Record in `.agents/memory/decisions.log`; write an ADR if an invariant or budget changed.
5. Human-gated: public communication and advisories.
