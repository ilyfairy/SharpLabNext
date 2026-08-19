# Development secrets

`internal-service-token.dev` is a public, development-only token used by
`compose.dev.yaml`. Override it with `SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE`
when a per-developer token is preferred. Never use this token or directory as a
production secret source.
