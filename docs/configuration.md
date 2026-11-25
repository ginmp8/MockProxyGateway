# Configuration reference and local acceptance

The authoritative requirements are [issue #1](https://github.com/ginmp8/MockProxyGateway/issues/1).
The original rule example is unchanged from its pinned baseline
`480af2c0386ef170ce991a7fee697866698426f8`. All tests use synthetic data and local
loopback upstreams. No deployment or public package publication is required.

## Startup settings (host configuration)

| Setting | Default | Contract |
| --- | --- | --- |
| Enabled | true | Applies only after explicit middleware registration. False leaves host routes untouched. |
| Prefix | /mock-proxy | Case-sensitive, non-root absolute path, no trailing slash, escaped segments or ambiguous characters. |
| RuleSource | File | File, Inline, Configuration; explicit provider overrides this setting. |
| ConfigurationFile | mock-proxy-gateway.json | Content-root-relative or absolute operator-owned path. |
| RulesJson | null | Complete serialized rule document when Configuration is selected. Explicit C# assignment is startup-only. |
| ProgrammaticRules | null | Typed `Func<JsonElement>` returning one complete document, explicit override. |
| ReloadOnChange | true | Poll selected source approximately every second. |
| MaxBodySizeBytes | 1048576 | Inspected JSON only; valid range 1–16777216. Other bodies stream. |
| MaxConcurrentRequests | 64 | Per gateway runtime; range 1–1024; excess receives 503. |
| ProxyTimeoutSeconds | 30 | YARP activity timeout, 1–300 seconds; connection timeout 10 seconds. Not an overall deadline. |
| AllowedEnvironments | Development | Other non-production names require explicit approval. Production always rejected. |
| AuthorizationPolicy | null | Existing named host policy, enforced before matching/forwarding. |
| FixtureRoot | null | Explicit trusted fixture directory, relative to content root or absolute. |
| AllowedDestinationAuthorities | [] | Explicit canonical hosts/ports approved by operator. |
| AllowedDestinationAddresses | [] | Explicit resolved IPs; each DNS result must be approved. |
| AllowLoopbackForTests | false | Only Development/Testing, literal loopback HTTP(S) destination with approved authority. |

Precedence: explicit middleware prefix > explicit C# option > host configuration
(including normal appsettings/environment-provider precedence) > library default.
Only the rule document is hot-reloaded. The host owns deployment and access control.
Registering DI alone is inert, including in Production.

## Select exactly one rule source

- **Dedicated file (default):** provide the complete document at the configured path.
  Copy it to publish output when needed, or mount it independently outside the image.
- **Inline:** set `RuleSource: Inline` and put the complete nested rule document in
  `MockProxyGateway:Rules` in one JSON-file configuration provider. The original JSON
  is read, preserving arrays, nulls and numbers. Multiple contributing providers,
  mixed serialized and nested sources, or flattened representations are rejected.
- **Serialized configuration:** set `RuleSource: Configuration` and
  `MockProxyGateway:RulesJson` to the complete document as a JSON string. Live values
  from the host configuration pipeline are polled. An explicit C# RulesJson assignment
  is fixed at startup, even when equal to the original bound value.
- **Explicit IConfiguration:** call `builder.Services.AddMockProxyGateway(configSection)`
  with a scalar section containing JSON, or a dedicated configuration containing only
  `RulesJson`. Do not pass a flattened rule catalog or unrelated root.
- **Programmatic:** call
  `builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(json))`.
  The caller may supply the latest complete immutable document from its own provider.
  The callback must be thread-safe, fast and host-owned. Exceptions reject a candidate
  and retain the last good snapshot; no request data may select a provider or file.

Every source compiles through the same validator. An invalid enabled startup fails.
Invalid reload retains the prior snapshot; rejected unchanged candidates are coalesced.
Use atomic replacement (write a new file then rename it into place), especially on
mounted files. The runtime captures fixture bytes with the rule snapshot; edit the
rule document to refresh a fixture or recover a rejected fixture candidate. Kubernetes
ConfigMap volume propagation is eventual; `subPath` mounts do not receive updates.
There is no cross-replica synchronization or administrative write endpoint.

## Rule document fields

See [the JSON Schema](rules.schema.json) and [complete example](../examples/mock-proxy-gateway.json).
The runtime additionally validates typed operands, numeric precision, duplicate
identifiers, resource limits, destination permissions, and fixture paths.

| Object | Fields |
| --- | --- |
| Root | schemaVersion (1), destinations, services |
| Destination | baseUrl, removeQuery |
| Service | enabled (true), default, rules, endpoints |
| Endpoint | method (case-insensitive HTTP token or *), path (exact, {id}, terminal {**rest}), default, rules |
| Rule | id (unique within service), priority (integer), enabled (true), expiresAt (optional UTC), conditions, action |
| Condition group | all / any (nonempty array), not (one condition) |
| Leaf | source, key when applicable, operator, value when applicable, quantifier (any), ignoreCase (false) |
| Proxy action | mode=proxy, destination (registered ID), pathRewrite (optional validated absolute-path override, never authority) |
| Mock action | mode=mock, response: statusCode, headers, delayMs, at most one of body (JSON), text, bodyFile |

Header names and query keys compare case-insensitively; cookie, route and JSON property
names use ordinal case-sensitive comparison. Cookie matching reads the original
Cookie header with the framework parser, preserving case-distinct and repeated
names; values are percent-decoded for matching only. Proxy headers are unchanged.
Method and content type compare
case-insensitively; content-type parameters are excluded. Path matching uses the
decoded path with case-sensitive segments; a trailing slash is equivalent for rule
selection. Dangerous encodings, dot segments, backslashes and ambiguous separators
are rejected. The forwarding path and query retain original valid percent encoding;
configured pathRewrite is already escaped and replaces only the path.

## Operators

| Operator | Operand and meaning |
| --- | --- |
| equals, notEquals | Typed scalar equality or inequality; no implicit conversion. |
| exists, notExists | No value operand; selector presence or absence. |
| isNull | No value operand; explicit JSON null, not missing. |
| contains, startsWith, endsWith | String operand and string selected value. |
| in, notIn | Nonempty scalar array (1–128); non-body sources use strings only. |
| regex | String pattern (max 256 characters); culture-invariant nonbacktracking engine, 25 ms per match. Unsupported constructs fail compilation. |
| greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual | Numeric JSON body only; exact representable decimal range. |
| between | Numeric JSON body only; two ordered numeric bounds, inclusive. |
| jsonEquals | Structural JSON equality, including objects and arrays. |

Text comparisons are ordinal unless ignoreCase is explicitly enabled for text.
Missing values do not satisfy ordinary positive or negative comparisons. For
multiple values, any requires one match; all requires at least one result and all
matching; none requires at least one result and none matching. Existence checks
are independent of quantifiers. JSONPath supports bounded RFC 9535 selectors:
`$.name`, `$['with.dot']`, `$["with.dot"]`, `$[0]`, `$[-1]`, `$[*]`, `$.*`,
`$[0:6:2]`, `$[0,2]` and `$..name`. At most 256 characters and 16 segments;
the product of selector counts across outer segments and any predicate query
cannot exceed four. A maximum
of two outer recursive descents is allowed. Up to two
outer filters are supported, including within selector unions, for example
`$.items[?@.active == true].value`, `$.items[0,?@.active == true]` or
`$.items[?@.active,?@.override]`. The existing union-product limit also applies
to mixed filter unions. Each filter expression is checked independently and
duplicate selections are preserved by the library. Nested-filter expressions
remain excluded from unions.
Filters accept relative and root paths, scalar literals, the six comparison
operators, existence (including wildcard and slice paths), AND/OR/NOT and up to eight
nested logical levels. `count(@.items[*])`, `value(@.items[0:1])` and
`count($.items[*])` accept bounded relative or root queries with name, index,
wildcard, slice or bounded union segments; comparisons still require singular
queries or value-returning functions. Slices cannot duplicate visits to a candidate
subtree; unions preserve duplicates and are charged for cumulative multiplicity.
Root queries receive the separate repeated-root work charge even inside functions.
Negated terms are explicitly grouped before library evaluation to preserve NOT
precedence. One outer recursive descent may precede, follow or contain an
outer filter; two outer descents with a filter incur the cubic precharge. A predicate
query may contain up to two descents, such as
`@..tag`, `count(@..tag)` or `count(@..nested..tag)`; such queries are non-singular.
Two predicate descents incur the cubic precharge; two outer and two predicate
descents cannot be combined. One nested relative filter inside the outer
predicate is admitted (e.g. `$.items[?@.nested[?@.active == true]]`), including
`count()` and `value()` over the nested results. Expressions containing a nested
filter cannot contain another outer filter, root references inside predicates,
unions or recursive descent;
additional nested levels, other functions and a third descent inside any query
remain rejected. The locked JsonPath.Net version is 2.2.0 (MIT). `length()` is not admitted
until RFC-correct Unicode scalar counting and bounded evaluation are verified
against the actual locked dependency.
The request budget is charged before evaluation for body nodes multiplied by
the union product; when descent is present, the charge also accounts for
descendant traversal and every subsequent segment. Filters conservatively
charge for predicate length, body nodes and body bytes before evaluation;
root references, nested filters, two outer descents and any descent combined with
filters additionally charge a
worst-case whole-input traversal and comparison for every possible candidate.
When two outer descents are combined with a filter or two descents occur inside
a predicate query, an additional body-node factor bounds repeated candidates or
repeated predicate traversals. This conservative cubic charge can reject otherwise
valid complex selectors on moderately sized inputs before library evaluation.
Filters also validate all numeric values in the inspected body once before the
first filter, rejecting values that cannot be represented exactly as decimals.
Numeric range comparisons reject unsupported decimal precision rather than rounding. Condition depth is at most 12; rule count 4096; condition nodes
16384; body nodes 16384; weighted evaluation budget 65536; at most 64 regex
evaluations per request. Body selector caching is bounded to twice the inspected
body byte limit. Inspection is lazy; malformed JSON 400, oversized 413, unsupported
media type 415; errors never select the proxy fallback.

Configuration document size 4 MiB; mock body size 4 MiB each and 16 MiB aggregate
per compiled snapshot; service and destination counts at most 256 each; endpoints
at most 1024 per service. Mock response status 200–599; delay 0–60000 ms;
ASCII response header values (HTAB allowed, max 8192 characters), validated content
type and no hop-by-hop/framing headers. No body for 204, 205, 304; HEAD suppresses
body transmission. Fixture paths are relative to a trusted operator-controlled
root with no traversal, symbolic links or reparse points; do not permit untrusted
writers. These are safety limits, not performance claims.

## Host policy and forwarding security

No identity services are required. When a host supplies a policy, register its own
authentication/authorization services, use authentication/authorization middleware
as required by the host, and set `AuthorizationPolicy` to the existing named policy.
The gateway explicitly evaluates that policy before rule evaluation; missing or
denied policy fails closed, regardless of pipeline order. It does not change
default schemes or policies, and preserves the incoming principal. Example:

```csharp
// The host has already registered its authentication scheme and policy.
builder.Services.AddMockProxyGateway(o => o.AuthorizationPolicy = "IntegrationTestAccess");
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseMockProxyGateway();
```

The gateway does not infer permission from scenario headers. Restrict the branch
with the host or trusted infrastructure, and apply deployment network egress
allowlists. Destinations are registered HTTPS authorities with explicitly allowed
addresses. Literal loopback HTTP is permitted only for explicitly enabled local
tests. Every new socket uses the checked address, not a second DNS lookup.
Redirect responses are returned to the consumer without automatic following.
The gateway does not implement retries or mock-to-proxy fallback. A request may
already have affected an upstream before an error; a 502/504 is not evidence that
a business mutation did not occur. Client cancellation propagates. All valid
end-to-end request headers, including Authorization, Cookie, API keys, arbitrary
custom headers, scenario headers, Forwarded and X-Forwarded-* pass through without
gateway-level filtering or identity synthesis. No configuration is required.
YARP handles HTTP protocol-specific headers, and the outbound Host uses the
registered destination authority. Each registered destination must be trusted
with the complete end-to-end header set. Upstreams must not treat untrusted
forwarded metadata as authenticated identity. Explicit removeQuery can still
remove configured query controls. No header policy settings are required or supported.
The YARP logger used by this gateway is isolated; structured gateway diagnostics
report service, rule, action, status, version, duration, correlation and failure
category without payloads. Host HTTP logging, .NET tracing and other observability
exporters remain the host's responsibility.

## Local example (synthetic loopback only)

Build Release first, then run `bash scripts/example-scenarios.sh`. The script starts
the standalone host on port 5182 and a local synthetic upstream on 5181, creates an
editable temporary copy of the pinned sample, and shuts both down on exit. It never
forwards to the example .test domains. Use a second terminal:

```bash
curl -i -H 'Content-Type: application/json' -H 'X-Test-Scenario: success' \
  -d '{"customer":{"riskScore":10}}' http://127.0.0.1:5182/mock-proxy/compliance/analysis
curl -i -H 'Content-Type: application/json' \
  -d '{"customer":{"riskScore":90}}' http://127.0.0.1:5182/mock-proxy/compliance/analysis
curl -i -H 'Content-Type: application/json' \
  -d '{"customer":{"riskScore":10}}' http://127.0.0.1:5182/mock-proxy/compliance/analysis
curl -i --max-time 1 -H 'Content-Type: application/json' -H 'X-Test-Scenario: timeout' \
  -d '{"customer":{"riskScore":10}}' http://127.0.0.1:5182/mock-proxy/compliance/analysis
```

Use `bash scripts/example-scenarios.sh --verify` for automatic representative curl
assertions. Run the complete integration suite for all matrix rows, including
local upstream call counts and concurrent reload. See the [acceptance matrix](acceptance-matrix.md).

For package-only consumption run `bash scripts/package-smoke.sh` after packing
Core. This creates a clean external Web SDK project using the local package feed.
For the optional Docker host, use `bash scripts/docker-smoke.sh` (Linux host
network, local upstream, read-only mounted rules); the image starts with the
gateway disabled unless the operator explicitly enables it. No public registry
publication is performed.
