# Security policy

## Supported versions

Which versions receive security fixes, and for how long, is part of one policy
with the rest of what a version number promises:
[docs/versioning.md](docs/versioning.md#how-long-a-version-is-supported). It is
there rather than here so that the two cannot drift apart.

While the line is 0.x it is the short version: fixes are applied to the latest
minor release, and nothing is back-ported.

## Reporting a vulnerability

Please do not open public issues for security problems.

Report vulnerabilities privately through the repository's security advisory
feature ("Report a vulnerability" under the Security tab). Include the affected
component, a description of the issue, and steps to reproduce if available.

You will receive an acknowledgement within five business days. We will work
with you on a fix and coordinate disclosure once a patched release is
available.

## Scope notes

- BYTEX handles exchange API credentials. Credentials are read from
  configuration or environment variables and are never logged. Any code path
  that could expose a credential in logs, error messages, or persisted state
  is in scope.
- Adapters talk to third-party exchanges over TLS. Certificate validation must
  never be disabled.
- Strategies are arbitrary user code executed in-process. BYTEX does not
  sandbox strategies; hosting environments that run untrusted strategies must
  provide their own isolation.
