# Acceptance trace

The following matrix is derived from the current issue #1, including its transparent-header forwarding revision; the pinned example
blob is `ca0f1bb118fe5b23c45e0e706e1cecaee6cd8465`. The source tests below must
execute at the actual feature HEAD. This document is a navigation and coverage
map, not a declaration that an unexecuted candidate passed.

### Scenario matrix (all analysis examples use POST /mock-proxy/compliance/analysis unless otherwise specified)

| Scenario | Request / configuration | Expected result |
| --- | --- | --- |
| Default real integration | No matching conditions | Proxy to compliance-real /analysis |
| Success | X-Test-Scenario: success | Mock 200 Approved |
| Business failure | JSON customer.riskScore = 80 or greater | Mock 422 Rejected |
| Unavailable | ?simulate=unavailable | Mock 503 |
| Timeout simulation | X-Test-Scenario: timeout; consumer timeout below 60 seconds | No upstream call; delayed 200 unless client cancels; consumer sees its own timeout |
| Rate limiting | X-Test-Scenario: rate-limited | Mock 429 with Retry-After: 30 |
| Unauthorized upstream simulation | X-Test-Scenario: unauthorized | Mock 401; simulates upstream behavior independently of any optional host access control |
| Pending | JSON customer.document = TEST-1001 | Mock 202 Pending |
| Alternate real integration | X-Test-Scenario: alternate | Proxy to compliance-v2 /v2/analysis |
| Explicit real override | X-Test-Scenario: real, even with ?simulate=unavailable | Proxy to compliance-real (priority 1000) |
| Combined conditions | X-Test-Session: session-a AND (simulate=success OR customer.type=Organization) AND NOT X-Disable-Scenario=true | Mock 200 composite |
| Array matching | JSON products contains an item with type TestProduct | Mock 200 array |
| Cookie matching | test-session=session-b | Mock 200 cookie |
| Overlapping scenarios | X-Test-Scenario: success and ?simulate=unavailable | Mock 503 (priority 900 > 400) |
| Equal winning priority | Configure two simultaneously satisfied rules at priority 400 with no higher match | Explicit 500, no proxy, record conflicting IDs |
| Disabled rule | X-Test-Scenario: disabled | Default proxy (assuming no other match) |
| Expired rule | X-Test-Scenario: expired | Default proxy (assuming no other match) |
| Route parameter | GET /mock-proxy/compliance/analysis/analysis-test-1001 | Mock 200 Pending |
| Endpoint default | GET /mock-proxy/compliance/health | Mock 200 Healthy, no condition needed |
| Catch-all | GET /mock-proxy/compliance/legacy/a/b | Proxy to compliance-v2 /legacy/a/b |
| Service-wide rule | Enable compliance-maintenance and send X-Test-Scenario: service-unavailable to an endpoint with no matching rule/default | Mock 503; disable to restore service default |
| Service fallback | GET /mock-proxy/compliance/other | Proxy to compliance-real |
| Second service | GET /mock-proxy/documents/anything | Proxy to documents-real |
| Unknown service | GET /mock-proxy/unknown/anything | 404, no outbound call |
| No default | Known service/endpoint with no applicable action | 404, no outbound call |
| Non-gateway route | GET /api/health | Existing application handles it unchanged |
| Session isolation | Send success and rejection requests concurrently with independent conditions | Each receives its own selected behavior |
| Hot toggle | Edit success-by-header enabled=false; reload completes | Subsequent matching requests use the next applicable rule/default, no redeploy |
| Invalid reload | Replace valid JSON with malformed JSON or invalid destination | Retain last known good rules; log validation failure without secrets |

## Executable scenario coverage

| Scenario rows | Observable checks |
| --- | --- |
| Default, success, rejection, unavailable, rate limit, unauthorized, pending, alternate, real override, compound, array, cookie, overlap, disabled, expired, route parameter, endpoint default, catch-all, fallback, second service, unknown service, normal route, timeout cancellation | ForwardingTests.PinnedExampleSelectsMockOrRealActionWithCorrectOutboundCount |
| Compound OR / NOT negative boundary | ForwardingTests.CombinedScenarioNegationAndAlternateBodyBranchDoNotLeakAcrossRequests |
| Equal priority, concurrent sessions, maintenance enable/disable, no-default and disabled service, hot toggle, invalid reload | ForwardingTests.ConcurrentRequestsAndHotRuleChangesRemainIsolated: observed snapshot activation followed by fresh HTTP status, body, headers, destination and outbound-count assertions; conflict IDs and last-known-good rejection diagnostic |
| Added and removed rules, concurrent readers, in-flight pin, source-equivalent reload | ConfigurationTests.AddingAndRemovingRulesKeepsConcurrentReadersOnWholeSnapshots, InFlightRequestRetainsOriginalSnapshotAcrossReload, SelectedSourcesReloadValidUpdatesAndRetainLastGood |
| Exact expiry boundary | ConfigurationTests.ExpiryUsesHostTimeProviderAtTheExactBoundaryWithoutReload |
| Successful short delay / cancellation of long configured delay | GatewayTests.DelayedMockReturnsConfiguredBodyAfterDelay; pinned example cancellation |
| Synthetic local curl requests | scripts/example-scenarios.sh --verify |

## AC evidence map

| Criterion | Primary source/test/gate |
| --- | --- |
| AC-01 | Solution/central props/lockfiles; CI restore/build/pack and inspect-package.py |
| AC-02 | scripts/package-smoke.sh: clean package-only host, omitted Prefix + parameterless registration, explicit custom middleware argument overriding configured prefix, segment boundary, unrelated routes, mock and proxy |
| AC-03 | src/MockProxyGateway.Host/Program.cs; ForwardingTests.StandaloneCompositionUsesCoreThroughWebApplicationFactory; Docker smoke |
| AC-04 | Scenario coverage table above; pinned baseline and actual outbound counts |
| AC-05 | GatewayTests.ExactMethodAndRouteSpecificityOverrideWildcardAndServiceDefault; pinned example and maintenance/no-default cases |
| AC-06 | ForwardingTests.ConcurrentRequestsAndHotRuleChangesRemainIsolated; controlled expiry; conflict diagnostics |
| AC-07 | GatewayTests.TypedOperatorBehavior, PresenceOperatorsDistinguishMissingNullAndPresentValue, BodyResultQuantifiersRequireNonemptyResultsAndCorrectMatches, TextComparisonIsOrdinalUnlessExplicitlyIgnoringCase, MultiValueQueryQuantifiers, RfcSelectorsSupportBoundedPathsAndFilters, MethodPathAndContentTypeConditionsRespectCaseAndShortCircuit, SourceNameCasingAndRepeatedValueQuantifiers, JsonPropertyNamesAreCaseSensitive; typed source tests and scenario matrix |
| AC-08 | GatewayTests.TypedOperatorBehavior (present equal/unequal operands and missing values), PresenceOperatorsDistinguishMissingNullAndPresentValue, InvalidConditionFailsActivation, ExcessiveConditionDepthFailsActivation, UnsupportedNumericPrecisionIsAnInspectionErrorNotAFalseMatch, ExcessiveBodyNodesFailClosed, JsonPathUnionWorkIsChargedBeforeAnyAction, JsonPathFilterWorkIsChargedBeforeAnyAction, ForwardingTests.FilterInspectionSelectsMockOrRealAndFailsClosedBeforeAnyAdditionalCall, RepeatedRootTraversalIsChargedSeparatelyFromRelativeFilterWork, PredicateUnionMultiplicityIsChargedBeforeForwarding, DescentFilterOverlapIsChargedBeforeForwarding, NestedRelativeFilterSelectsMockOrRealWithoutExtraOutboundCalls, RepeatedPathWorkIsRejectedBeforeAnyOutboundCall (including cubic traversal and mixed outer filter unions), RegexEvaluationBudgetRejectsExcessiveFanoutWithoutDefaultAction; RegexBoundaryTests.ActualEngineTimeoutIsClassifiedAsInspectionFailure |
| AC-09 | ForwardingTests.PinnedExampleSelectsMockOrRealActionWithCorrectOutboundCount, RawPathAndRemainingQueryEncodingReachRealUpstreamUnchanged, NonInspectedBinaryBodyStreamsBeforeClientFinishesSending, InspectedJsonPreservesExactBytesAndBusinessHeadersAtRealUpstream, AllEndToEndHeadersReachSelectedUpstreamWithoutConfiguration |
| AC-10 | Pinned mock scenario tests, GatewayTests.HeadAndEmptyStatusesDoNotWriteResponseBodies, DelayedMockReturnsConfiguredBodyAfterDelay; ConfigurationTests.FileFixtureIsReadOnlyFromTrustedRoot |
| AC-11 | ConfigurationTests.SelectedSourcesReloadValidUpdatesAndRetainLastGood, InFlightRequestRetainsOriginalSnapshotAcrossReload, AddingAndRemovingRulesKeepsConcurrentReadersOnWholeSnapshots, ExpiryUsesHostTimeProviderAtTheExactBoundaryWithoutReload |
| AC-12 | ForwardingTests.RequiredInspectionFailureCannotReachDefaultProxy, FilterInspectionSelectsMockOrRealAndFailsClosedBeforeAnyAdditionalCall, FailedMutationIsNotRetriedOrReplacedWithMock |
| AC-13 | ForwardingTests.OptionalNamedPolicyProtectsRealForwardingAndPreservesSeparateHostPolicy, DeniedDnsAddressNeverEstablishesEvenATcpConnection, UnsafeDestinationRejectedEvenWithExplicitAuthority, AllEndToEndHeadersReachSelectedUpstreamWithoutConfiguration (including distinct default/alternate loopback authorities, credential and custom headers, zero calls to unselected destinations, no-call mock, Host and protocol headers, redirects), raw-path tests, UnapprovedAuthorityAndUnknownDestinationFailBeforeAnyRequest, InvalidRewriteCannotEscapeRegisteredDestination, RequestSuppliedUrlRemainsDataAndCannotSelectAnAuthority; ConfigurationTests.RegistrationWithoutMiddlewareIsInertEvenInProduction, MissingConfiguredPolicyDeniesGatewayWithoutAffectingHostRoutes, SymlinkFixtureOutsideTrustedRootIsRejected; GatewayTests.InvalidMockHeadersCannotActivate, ProductionActivationFailsEvenWhenAllowlisted; ConfigurationTests.ConcurrencyLimitFailsClosedAndReleasesAfterCancellation |
| AC-14 | ForwardingTests.GatewayDiagnosticsDoNotLogUpstreamQueryOrCredentials, ConflictAndReloadDiagnosticsIdentifyRulesAndActiveVersionWithoutSensitiveValues |
| AC-15 | README, configuration.md, rules.schema.json, sample, curl and Docker scripts |
| AC-16 | .github/workflows/ci.yml and package inspection/smoke |
| AC-17 | Host Dockerfile and scripts/docker-smoke.sh |
| AC-18 | ConfigurationTests.AllSourcesPreserveTypedValuesAndShareValidation, SelectedSourcesReloadValidUpdatesAndRetainLastGood, EnvironmentAndProgrammaticOptionsAndExplicitPrefixHavePrecedence, ExplicitSerializedRulesRemainAuthoritativeEvenWhenInitialValueEqualsHostValue, ConflictingConfiguredRuleSourcesAreRejected, ConflictingExplicitRuleSourcesAreRejected, MissingSelectedFileAndDefaultEnvironmentGateRejectActivation; GatewayTests.DisabledGatewayDoesNotRequireFile |

Current limitations are explicit in configuration.md. The bounded JSONPath subset
is not unrestricted RFC 9535; the gateway uses the actual JsonPath.Net parser and
evaluation for supported selectors, including slices, bounded unions, up to two recursive descents and up to two outer filters. Case-distinct and repeated cookie names are exercised through the public middleware. Relative and root wildcard/slice existence and bounded `count()`/`value()` have focused middleware and real-upstream regression cases, including repeated-root budget rejection. Bounded predicate unions preserve duplicate nodes and have a separate real-upstream budget oracle. One outer descent combined with a filter has positive/negative middleware cases and a separate real-upstream overlapping-traversal budget oracle. Bounded predicate descent is exercised for existence, count/value and combined outer descent with a separate real-upstream work limit. One nested relative filter is supported, including outer unions and a second outer filter, with middleware cases and a real-upstream budget oracle. Other functions, deeper nesting and combinations exceeding the documented traversal/union limits remain unsupported. The regex timeout oracle uses an actual
engine timeout at the shared classification boundary, while production rules
use nonbacktracking patterns and bounded evaluation. The DNS oracle uses a local
TCP listener to detect forbidden connection attempts; external DNS rebinding
and deployment network policy are not exercised against real systems.
