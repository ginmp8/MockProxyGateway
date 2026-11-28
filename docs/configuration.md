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
| AllowedJsonMediaTypes | [] | Exact additional `+json` media types for body inspection, e.g. `application/problem+json`. Maximum 64 entries, each up to 256 characters; no wildcard or parameters. |
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
Only the rule document is hot-reloaded. Activation captures a private copy of startup settings, including destination and environment arrays. Mutating the host's `IOptions<GatewayOptions>` value after activation does not change the authorization policy, resource limits, selected provider or approved destinations. The originally selected provider can still publish updated rule documents. The host owns deployment and access control.
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
  from the host configuration pipeline are polled. The serialized section must be
  a single scalar: children contributed by any configuration provider are invalid,
  even if a valid serialized value also exists. File and Inline reject any competing
  serialized section, including flattened children. For configured source selection,
  omit unused rule-source keys entirely: an explicitly null competing `Rules` or
  `RulesJson` key is still a conflicting definition. An explicit C# RulesJson assignment
  (including host `Configure<GatewayOptions>` and `PostConfigure<GatewayOptions>`)
  is fixed at startup, even when equal to the original bound value. The built-in
  configuration-section binding alone does not count as an explicit C# assignment.
  Explicit assignment to `null` is still a serialized source definition, not an
  omission; it is rejected with File, Inline, or another explicitly supplied
  provider. Omit the assignment entirely when choosing those sources.
- **Explicit IConfiguration:** call `builder.Services.AddMockProxyGateway(configSection)`
  with a scalar section containing JSON and no children, or a dedicated configuration containing only
  `RulesJson`. A scalar section with a competing child is invalid even when that child has a null value.
  Do not pass a flattened rule catalog or unrelated root.
- **Programmatic:** call
  `builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(json))`.
  The caller may supply the latest complete immutable document from its own provider.
  The callback must be thread-safe, fast and host-owned. Exceptions reject a candidate
  and retain the last good snapshot; no request data may select a provider or file.
  Select a single explicit provider. A subsequent distinct provider assignment
  (including host Configure/PostConfigure) cannot replace or clear an already
  selected typed or supplied IConfiguration source; enabled activation rejects
  the conflict rather than choosing by registration order.

Every source compiles through the same validator. Serialized documents must be
valid Unicode and fit the 4 MiB UTF-8 document limit before parsing, including
leading/trailing whitespace in explicit configuration sources. Unpaired UTF-16
surrogates are rejected, never silently replaced with U+FFFD. An invalid enabled startup fails.
Invalid reload retains the prior snapshot; rejected unchanged semantic-validation candidates are coalesced. Transient I/O or access failures retry at the existing polling cadence without repeated failure logs.
Use atomic replacement (write a new file then rename it into place), especially on
mounted files. The runtime captures fixture bytes with the rule snapshot; edit the
rule document to refresh an already active fixture. A temporarily unavailable fixture in a rejected candidate is retried without another rule edit. Kubernetes
ConfigMap volume propagation is eventual; `subPath` mounts do not receive updates.
There is no cross-replica synchronization or administrative write endpoint.

## Rule document fields

See [the JSON Schema](rules.schema.json) and [complete example](../examples/mock-proxy-gateway.json).
The runtime additionally validates typed operands, numeric precision, duplicate
identifiers, resource limits, destination permissions, and fixture paths.
Identifiers and HTTP tokens must match the entire original string: trailing
line breaks are invalid, and endpoint methods are validated as ASCII tokens
before case normalization. Mock response header names and header/cookie selector keys must be HTTP tokens. Route selector keys must use the same identifier grammar as route parameters. Malformed selector names reject the whole candidate instead of becoming non-matches.
All strings in the rule document, including nested operands and raw mock bodies,
must decode as valid Unicode. Explicit JSON null is preserved in body values
and typed operands; optional string metadata such as quantifier and pathRewrite
must be omitted rather than set to null. Expiry uses an ISO 8601 UTC timestamp
with seconds and an explicit Z or +00:00 offset (parsed by System.Text.Json).

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
names; values are percent-decoded for matching only. Required cookie inspection rejects
malformed cookie syntax with a sanitized 400 instead of treating it as a missing value.
A required content-type selector likewise rejects a malformed present Content-Type with
400; an absent header remains missing. Media ranges such as `*/*` and `application/*`
are not concrete Content-Type values and are rejected by required content-type matching.
Body inspection retains its own 415 contract.
These checks are lazy rule evaluation, not a proxy header filter. Proxy headers are unchanged.
Method and content type compare
case-insensitively, including the regex operator; content-type parameters are excluded. Path matching uses the
decoded path with case-sensitive segments; a trailing slash is equivalent for rule
selection. Dangerous encodings, dot segments, backslashes and ambiguous separators
are rejected. Valid percent escapes in the request's gateway prefix or service
alias are resolved against the same decoded, case-sensitive segments. Only those
routing segments are stripped; the forwarding path and query retain original
valid percent encoding. The configured pathRewrite is already escaped and
replaces only the path. Unsafe original
gateway targets are rejected even if host normalization has moved the request
outside the gateway prefix, including percent-encoded prefix characters.

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
duplicate selections are preserved by the library. Nested relative filters may appear in outer unions or alongside a second outer
filter. The same outer union-product cap and quadratic precharge apply.
Filters accept relative and root paths, scalar literals, the six comparison
operators, existence (including wildcard and slice paths), AND/OR/NOT and up to eight
nested logical levels. `count(@.items[*])`, `value(@.items[0:1])` and
`count($.items[*])` accept bounded relative or root queries with name, index,
wildcard, slice or bounded union segments; comparisons still require singular
queries or value-returning functions. Slices cannot duplicate visits to a candidate
subtree; unions preserve duplicates and are charged for cumulative multiplicity.
Root queries receive the separate repeated-root work charge even inside functions.
Negated terms are explicitly grouped before library evaluation to preserve NOT
precedence. Ordered JSONPath comparisons use Unicode scalar order rather than
UTF-16 code unit order; two missing query results are equal (and satisfy
non-strict ordering), while neither strictly precedes the other. The local
ordered comparator preserves typed values, relative/root scope, count/value
operands and the existing traversal limits. See RFC 9535 section 2.3.5.2. One outer recursive descent may precede, follow or contain an
outer filter; two outer descents with a filter incur the cubic precharge. A predicate
query may contain up to two descents, such as
`@..tag`, `count(@..tag)` or `count(@..nested..tag)`; such queries are non-singular.
Two predicate descents incur the cubic precharge; two outer and two predicate
descents cannot be combined. One nested relative filter inside the outer
predicate is admitted (e.g. `$.items[?@.nested[?@.active == true]]`), including
`count()` and `value()` over the nested results. Expressions containing a nested
filter cannot contain predicate unions or predicate descent. One outer descent may be combined with a nested filter under cubic work and quadratic regex reservations; two outer descents with nested filters remain excluded. Bounded root-driven
enclosing queries use the separate cubic work profile described below. The single nested predicate may refer to the original
document root, including ordinary existence and comparison predicates;
additional nested levels and a third descent inside any query remain rejected.
The locked JsonPath.Net version is 2.2.0 (MIT). An outer filter
`length(@.name) == 2` (or a root/singular indexed query or a scalar literal with any of the six
comparison operators and a singular query or scalar literal) uses a gateway-local
selector: string length counts Unicode scalar values, object/array length
counts members, and unsupported or missing values produce Nothing. The
[pinned upstream length implementation](https://github.com/json-everything/json-everything/blob/f53d4d27eeed6a73d377bf4188175f0b8a46f856/src/JsonPath/LengthFunction.cs)
counts graphemes, so the gateway does not evaluate that function or modify
the process-global registry. The same traversal/byte budgets apply. Logical combinations with ordinary predicates and the other private functions are supported in outer filters and one nested relative existence query. String literals are decoded and counted once during compilation. Other scalar inputs produce Nothing. A missing comparison query also produces Nothing: two Nothing operands compare equal, whereas an explicit null is a value and differs from Nothing. Numeric comparisons remain exact; no text-to-number conversion occurs. A scalar literal or singular query can also appear on the left of a length comparison; operand order is preserved for all six operators. Two bounded length calls may also be compared, with the same Nothing semantics and traversal charges. Length can be compared with bounded `count()` and `value()` calls on either side of the operator, using the same non-overlapping query restrictions and repeated traversal precharge. Count always produces a numeric value (zero for an empty nodelist), whereas value produces Nothing unless exactly one node is selected. Selected null, containers and strings are not coerced to numbers. A direct non-singular value operand remains rejected; `length(value(@.names[*]))` explicitly converts a bounded, non-overlapping relative or root nodelist to a ValueType. Its argument may use wildcard and slice selectors, but not a predicate, union or descent. Exactly one node supplies its value, including arrays and objects; zero or multiple nodes yield Nothing. This composition receives the repeated traversal precharge. Relative, non-overlapping conversions are also admitted within a single nested filter; the quadratic work and regex visit limits remain unchanged. A single nested private relative filter may also be used as a `count()` or `value()` operand on either side of a comparison with a literal, singular relative or document-root query, or a second bounded relative `count()`/`value()` call without another nested filter. Count comparisons accept typed scalar literals or singular relative queries without coercion: equality to a string, boolean or null is false, inequality is true, and ordered comparisons between unlike types are false. Value supports typed scalar literals and structural comparison of selected containers, with zero or multiple nodes treated as Nothing.
`match(@.name, 'test-.*')` and `search(@.name, '[A-Z]+')` use private
selectors with constant scalar pattern arguments and singular relative/root queries or scalar-literal inputs. Non-string inputs or patterns produce logical false. String input/pattern pairs are evaluated once at configuration compilation; a timeout rejects that candidate configuration (and preserves the last known good snapshot on reload). The same conservative regex visit budget is retained. The input may also be `value()` over the same bounded nodelist: zero/multiple nodes, null and non-string values yield logical false; one string is evaluated by the precompiled pattern. A single bounded `count()` or `length()` operand may appear as an argument of `length()`, `match()` or `search()`, including either regex argument. Count produces a number; length produces a number or Nothing, so the outer length yields Nothing and regex functions yield logical false. The inner argument is fully validated before folding, with the same relative/root restrictions and conservative work charges. Further numeric function nesting is rejected. Dynamic string pattern arguments remain unsupported.
Supported patterns: Unicode literals (including literal ^ and $), scalar dot
(excluding CR/LF), escaped literals, positive and negated Unicode scalar literal classes/ranges,
and quantifiers (*, +, ?, {n}, {n,}, {n,m}), with explicit repetition bounds at
most 64 and a conservative product of explicit repetition expansion factors
at most 256 (open-ended ranges charge minimum plus one). Noncapturing translation of grouping and alternation preserves the
I-Regexp Boolean semantics; group depth is limited to eight. Classes have at most 32 intervals or category members, including boundary literal hyphens. Unicode general-category escapes (`\p{L}`, `\P{Nd}`, etc.) follow RFC 9485, including supplementary scalars and mixed or negated classes. Only actual Unicode property escapes select the private alphabet; escaped literal backslashes followed by p or P use the ordinary translation. Property patterns use a private finite alphabet derived from scalar intervals and Unicode categories (the .NET runtime Unicode data), limited to 64 atoms, 256 interval boundaries, 512 symbols, a million classification operations and a 4096-character translated pattern; no global function changes. Match consumes
the entire string; search looks for a substring. Missing and non-string values
do not match. The nonbacktracking engine has a 25 ms timeout and candidate
visits are reserved from the shared 64-evaluation budget before evaluation.
Regex predicates can be combined with one or two outer descents. One descent retains the linear candidate reservation including selector-union multiplicity; two descents conservatively reserve the square of the body-node count because overlapping subtrees can revisit candidates. The existing quadratic/cubic work bounds also apply, before any action. One outer descent with a single nested filter conservatively reserves quadratic regex visits and cubic work before evaluation; two outer descents with nested filters and descent inside nested predicates remain excluded. A single nested relative existence query such as `$.items[?@.nested[?match(@.name, 'x')]]` uses compiled relative selectors starting at the outer candidate while retaining the original document root for inner predicates. Root-driven enclosing queries are supported for existence and count/value comparisons with a separate cubic work and quadratic regex visit precharge; predicate unions, descent and deeper nesting remain rejected. Bounded length comparison operands may use root or candidate queries, including bounded value conversion, in either order. Nested private count/value comparisons use the same isolated query and shared budgets; singular relative comparison queries, scalar-correct `length()` of a singular relative query or literal, and bounded relative count/value operands use the same candidate scope. A count/value argument may be non-singular; a bare comparison query must remain singular. A `length()` operand opposite a nested count/value comparison may use a singular candidate-relative or document-root query, or `length(value(...))` over a bounded non-overlapping relative or root query. These operands are covered by the quadratic charge for relative enclosing selections and the cubic charge for root-driven enclosing selections. Predicate unions, descent and additional nested filters remain excluded. Private length/match/search functions inside the nested predicate may use bounded relative or root value queries. For example, `$.items[?@.nested[?length(@.name) == $.limit]]` uses the inner candidate name and the document-level limit. Root references inside the nested predicate retain the quadratic whole-input charge. Ordinary comparisons such as `$.items[?@.nested[?@.name == $.label]]`, root existence, and count/value of the bounded relative selection use the same local selector and document scope. A singular document-root query or bounded root `count()`/`value()` opposite a nested count/value operand uses the original document root under the same quadratic precharge (cubic for a root-driven enclosing query). Root function operands may use wildcard or slice selectors, but no predicate, union or descent. Other outer root references, descent, predicate unions, unsupported function operands and a second nested filter remain rejected. A root-driven enclosing query starts at the original document root, not the outer candidate, using the same local compiled selectors. For example, `$.items[?count($.nested[?@.active]) == 1]` selects outer items using the shared root collection. Cubic work accounts for repeated inner traversal and quadratic regex precharge accounts for repeated inner candidate visits. Outer compound predicates are limited to 16 leaves and eight logical nesting levels; AND, OR and NOT preserve precedence and short-circuit evaluation. Reserve potential visits for every regex leaf before evaluation, even if skipped by short circuit.
The request path is split once for method-eligible endpoint candidates. Endpoint selection charges inspected segments, literal comparison lengths and catch-all materialization against the same request budget as condition matching; an exhausted budget returns a sanitized 400 without evaluating a default action. Method-ineligible endpoints do not consume path inspection work.

The request budget is charged before evaluation for body nodes multiplied by
the union product; when descent is present, the charge also accounts for
descendant traversal and every subsequent segment. Filters conservatively
charge for predicate length, body nodes and body bytes before evaluation;
root references, nested filters, two outer descents and any descent combined with
filters additionally charge a
worst-case whole-input traversal and comparison for every possible candidate.
When two outer descents are combined with a filter, one outer descent is combined with a nested filter, or two descents occur inside
a predicate query, an additional body-node factor bounds repeated candidates or
repeated predicate traversals. This conservative cubic charge can reject otherwise
valid complex selectors on moderately sized inputs before library evaluation.
Filters also validate all numeric values in the inspected body once before the
first filter, rejecting values that cannot be represented exactly as decimals.
RFC 9535 blank space (space, tab, carriage return and line feed) before relative or root query segments inside filters is accepted without changing quoted property names or the existing traversal and evaluation limits. Other Unicode whitespace is rejected as a grammar separator, but preserved when it belongs to a quoted or shorthand Unicode member name. Shorthand names containing Unicode whitespace are compiled as equivalent bracket selectors so the underlying parser cannot trim them into a different member name. Array indexes and slice bounds/steps use ASCII decimal digits only, as required by RFC 9535 sections 2.3.3 and 2.3.4. Non-ASCII decimal digits remain valid in quoted or shorthand member names, but are rejected in numeric selector tokens before the pinned parser can misinterpret them. Every bracketed slice component must contain a complete integer or be genuinely omitted: a standalone minus sign is invalid even in an unfiltered path. Legitimately omitted components, negative indexes and slice bounds, zero steps, and quoted member names remain supported. Adjacent Unicode and short string escapes in quoted selectors are compiled as equivalent escape sequences to preserve RFC 9535 member names with the pinned parser. Isolated or mismatched surrogate escapes in quoted member selectors and filter literals reject the complete candidate at load time, before publication; valid supplementary scalar pairs remain supported. Leading/trailing query whitespace and relative top-level queries remain invalid; whitespace between segments is accepted.

Numeric range comparisons reject unsupported decimal precision rather than rounding. Repeated comparisons of numeric JSON tokens (including equality and membership) charge their raw token length against the shared evaluation budget, even when the selected value is cached. Condition depth is at most 12; rule count 4096; condition nodes
16384; body nodes 16384; weighted evaluation budget 65536; at most 64 regex
evaluations per request. Query and cookie inspection charges the complete
inspected input and entry count, including unrelated names. Content-type
inspection charges the complete header and parameter count even though matching
uses only the media type. Selected text values
are charged before serialization even for presence operators. Budget exhaustion
returns a sanitized 400 without a proxy fallback. An action that does not inspect
these sources does not incur those inspection charges or modify the values.
Body inspection always admits `application/json`; other structured-suffix JSON media types require explicit startup admission in `AllowedJsonMediaTypes`. Matching media type names is case-insensitive and ignores request parameters. This setting is not a proxy filter: requests that do not need body inspection retain their original media type and bytes. Content-type conditions can still inspect any valid media type.
Body selector caching is bounded to twice the inspected
body byte limit. Inspection is lazy; malformed JSON (including duplicate object
names, invalid UTF-8 and unpaired Unicode surrogate escapes) returns 400,
oversized bodies 413, unsupported media type 415; HTTP client errors raised by the host while reading a required body retain their sanitized status (including a stricter host body limit). Required inspection starts at the beginning of the buffered request body even if a preceding host middleware left a seekable stream partially read; forwarding uses those same bytes. If the request declares a content length, required inspection rejects an inconsistent buffered length with a sanitized 400 before choosing any action. Host middleware that consumes a non-seekable body must buffer it first and make it available to downstream middleware; with unknown-length framing, a previously consumed prefix cannot reliably be detected or recovered by the gateway. A host that replaces the request body must also maintain its framing metadata. Uninspected requests retain streaming behavior. Errors never select the proxy
fallback. Bodies not requiring inspection are not decoded by the gateway.

Configuration document size 4 MiB; mock body size 4 MiB each and 16 MiB aggregate
per compiled snapshot; service and destination counts at most 256 each; endpoints
at most 1024 per service. Mock response status 200–599; delay 0–60000 ms;
ASCII response header values (HTAB allowed, max 8192 characters), validated content
type without media ranges and no hop-by-hop/framing headers (including `Proxy-Connection`). No body for 204, 205, 304; HEAD suppresses
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
The gateway-owned HTTP handler disables automatic activity-header propagation:
existing trace and baggage headers remain ordinary incoming headers, and ambient
host context is never injected when those headers are absent. Gateway correlation
logging remains available; this does not change host-wide tracing settings.
YARP handles HTTP protocol-specific headers, and the outbound Host uses the
registered destination authority. Each registered destination must be trusted
with the complete end-to-end header set. Upstreams must not treat untrusted
forwarded metadata as authenticated identity. Explicit removeQuery can still
remove configured query controls. Removal names are compiled into an immutable,
ordinal case-insensitive lookup at snapshot creation, so each query field does
not scan the configured catalog. Only decoded matching names are removed; the
original encoding, order, repeated values and empty retained wire fields are preserved.
The query delimiter is removed only when no fields remain.
No header policy settings are required or supported.
Early gateway rejections (missing service segment, invalid target, unsupported
protocol, and concurrency limit) emit the same redacted completion event with
the current configuration version and correlation ID. Service, rule and action
are fixed placeholders until resolution; raw paths, queries and headers are not
included. Unrelated requests outside the gateway prefix are not logged by the
gateway; unsafe original gateway targets remain gateway rejections even when
host normalization moves their decoded path outside the prefix.

The YARP logger used by this gateway is isolated; structured gateway diagnostics
report service, rule, action, status, version, duration, correlation and failure
category without payloads. A forwarding timeout before response headers is
classified as `proxy-timeout`; request cancellation as `cancelled`; cancellation
during body copying as `proxy-body-cancelled` (without guessing whether the client
or an activity timeout caused it); other forwarding failures as `proxy-transport`.
A real upstream HTTP 502/504 is a completed exchange, not a gateway transport
failure. Host HTTP logging, .NET tracing and other observability
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
