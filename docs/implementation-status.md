# Implementation checkpoint

This branch is in active development for issue #1. It is not release-ready.

This increment adds Core middleware, an optional host, initial integration tests,
central build configuration, rule compilation/resolution, local mock responses,
destination-restricted YARP forwarding and immutable rule-document polling.
No runtime or package validation is claimed.

## Current boundaries

- Explicit non-production middleware opt-in; no mandatory identity infrastructure.
- Proxy authorities must be explicitly configured (AllowedDestinationAuthorities).
  Non-loopback DNS results must match AllowedDestinationAddresses. Connections
  use a checked IP, not a second name lookup. Literal HTTP loopback is allowed
  only with explicit AllowLoopbackForTests in Development or Testing. Deployment
  network egress policy is still required.
- Known credential headers are removed unless explicitly permitted for the
  destination; custom credential headers require explicit removeHeaders review.
  No automatic redirects or fallback.
- Matching paths are case-sensitive; trailing slash is equivalent for rule
  selection. Rewrite is path-only.
- Initial JSONPath subset: bounded property, array index and wildcard selectors.
  Recursive/filter/function expressions are rejected. Numeric range comparisons
  use decimal and reject unsupported range.
- File, serialized JSON configuration and typed programmatic document provider
  share validation. Inline nested source, arbitrary provider parity and fixtures
  remain unfinished.
- Default standalone host is disabled. For a local demo set
  ASPNETCORE_ENVIRONMENT=Development and MockProxyGateway__Enabled=true,
  then request GET /mock-proxy/demo/health.

## Remaining gates

The available sandbox lacks dotnet and Docker and could not resolve GitHub DNS.
The first CI invocation must produce authentic lockfiles; the committed-lockfile
gate intentionally fails until they are checked in. Build/test results, complete
scenario and security tests using real local upstream, package-only consumption,
Docker smoke tests and JSON Schema remain unverified. No criterion is marked
complete from unexecuted tests.

JsonPath.Net 2.2.0 is the MIT-licensed candidate rather than the 3.x binary with
maintenance-fee EULA. Confirm resolved dependency licenses and audit before
release. All packages are centrally pinned; no transitive audit is claimed yet.
