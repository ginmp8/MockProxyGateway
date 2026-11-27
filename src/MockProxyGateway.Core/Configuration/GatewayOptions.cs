using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MockProxyGateway;

/// <summary>Host-owned startup settings. Rule data is selected separately.</summary>
public sealed class GatewayOptions
{
    /// <summary>Enable explicitly registered middleware.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Gateway path prefix.</summary>
    public string Prefix { get; set; } = "/mock-proxy";
    /// <summary>File, Inline or Configuration rule document source.</summary>
    public string RuleSource { get; set; } = "File";
    /// <summary>Absolute or content-root-relative rule file.</summary>
    public string ConfigurationFile { get; set; } = "mock-proxy-gateway.json";
    private string? _rulesJson;
    internal int RulesJsonAssignments { get; private set; }
    internal bool HasExplicitRulesJson { get; set; }

    /// <summary>Complete serialized JSON for the Configuration source.</summary>
    public string? RulesJson
    {
        get => _rulesJson;
        set
        {
            _rulesJson = value;
            RulesJsonAssignments++;
        }
    }
    /// <summary>Explicit typed programmatic provider, overriding configured source selection.</summary>
    public Func<JsonElement>? ProgrammaticRules { get; set; }
    /// <summary>Check the selected document for updates.</summary>
    public bool ReloadOnChange { get; set; } = true;
    /// <summary>Maximum inspected JSON body bytes.</summary>
    public int MaxBodySizeBytes { get; set; } = 1_048_576;
    /// <summary>Additional exact structured-suffix JSON media types admitted for body inspection.</summary>
    public string[] AllowedJsonMediaTypes { get; set; } = [];
    /// <summary>Maximum concurrent gateway requests.</summary>
    public int MaxConcurrentRequests { get; set; } = 64;
    /// <summary>YARP activity timeout in seconds.</summary>
    public int ProxyTimeoutSeconds { get; set; } = 30;
    /// <summary>Allowed non-production environments; null uses Development only.</summary>
    public string[]? AllowedEnvironments { get; set; }
    /// <summary>Optional existing host authorization policy.</summary>
    public string? AuthorizationPolicy { get; set; }
    /// <summary>Trusted fixture directory; relative to the host content root unless absolute.</summary>
    public string? FixtureRoot { get; set; }
    /// <summary>Approved upstream URI authorities (including non-default ports).</summary>
    public string[] AllowedDestinationAuthorities { get; set; } = [];
    /// <summary>Explicit approved upstream IP addresses, checked at connection time.</summary>
    public string[] AllowedDestinationAddresses { get; set; } = [];
    /// <summary>Allow explicit literal loopback upstreams in Development or Testing.</summary>
    public bool AllowLoopbackForTests { get; set; }

    internal GatewayOptions CopyForStartup()
    {
        // The host may retain its mutable IOptions value and input arrays.
        // The runtime must never observe later changes to startup-only controls.
        var copy = (GatewayOptions)MemberwiseClone();
        copy.AllowedEnvironments = AllowedEnvironments?.ToArray();
        copy.AllowedJsonMediaTypes = AllowedJsonMediaTypes?.ToArray() ?? [];
        copy.AllowedDestinationAuthorities = AllowedDestinationAuthorities.ToArray();
        copy.AllowedDestinationAddresses = AllowedDestinationAddresses.ToArray();
        return copy;
    }
}

internal sealed class GatewayStartupOptions
{
    public GatewayStartupOptions(IOptions<GatewayOptions> options) => Value = options.Value.CopyForStartup();

    internal GatewayOptions Value { get; }
}
