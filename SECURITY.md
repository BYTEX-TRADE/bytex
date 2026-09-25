# Security policy

## Supported versions

Security fixes are applied to the latest minor release on `main`.

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
