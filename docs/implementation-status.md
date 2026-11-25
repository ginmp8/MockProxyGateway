# Implementation status

Initial implementation is tracked in issue #1 and draft PR #2. This document describes the current source, not a release acceptance certificate.

- Core middleware and thin optional host; explicit non-production activation and configurable prefix.
- Versioned rules, deterministic endpoint and rule precedence, conditional matching, mock responses and destination-restricted YARP forwarding.
- Typed dedicated file, single JSON-file inline rules, serialized configuration and programmatic providers; all share semantic validation. Polling swaps a validated snapshot atomically, retaining the last valid version when reload fails.
- Trusted fixture files captured during snapshot compilation; no untrusted writes to fixture roots.
- Authority and resolved IP allowlists, checked socket destination, disabled redirects and system proxy, credential filtering and bounded body evaluation. Continue to enforce host-specific egress and logging policies.
- Bounded JSONPath subset (simple property, array index and array wildcard). Unrestricted selectors are not supported. Full raw-target encoding preservation, streaming behavior and diagnostic privacy still require executable evidence.
- .NET 10 centralized build, real CI-generated lockfiles, integration tests, rule schema, package-only consumer and Docker smoke workflows.
- JsonPath.Net 2.2.0 is selected as the MIT-licensed binary rather than the 3.x maintenance-fee EULA. Final transitive licensing and vulnerability gates remain.

The runner has proven locked restore, but the build has been blocked by CA1873 in gateway completion logging. The latest fix computes elapsed duration only when logging is enabled and passes the precomputed value. Check the exact feature HEAD CI for the actual result. No tests, package smoke or Docker smoke are claimed as passing until they execute successfully.

Use the issue and PR checkpoint to track all eighteen acceptance criteria. No package publication, merge, tag or release is authorized.
