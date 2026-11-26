using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MockProxyGateway.Configuration;

internal sealed partial class RuleStore : IAsyncDisposable
{
    private const int MaximumDocumentBytes = 4_194_304;
    private readonly GatewayOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RuleStore> _logger;
    private readonly CancellationTokenSource _stop = new();
    private RuleSnapshot _snapshot;
    private string? _attemptedHash;
    private readonly string? _explicitSerialized;
    private readonly bool _hasExplicitSerialized;
    private string? _rejectedHash;
    private bool _lastFailed;
    private readonly Task _reload;

    public RuleStore(IOptions<GatewayOptions> options, IHostEnvironment environment, IConfiguration configuration, ILogger<RuleStore> logger)
    {
        _options = options.Value;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
        _hasExplicitSerialized = _options.HasExplicitRulesJson ||
            !string.Equals(_options.RulesJson, configuration["MockProxyGateway:RulesJson"], StringComparison.Ordinal);
        if (_hasExplicitSerialized)
            _explicitSerialized = _options.RulesJson;
        if (_options.AllowLoopbackForTests && !environment.IsDevelopment() &&
            !string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Loopback forwarding is permitted only in Development or Testing.");
        try
        {
            var bytes = ReadDocument();
            _snapshot = new RuleCompiler(_options, _environment.ContentRootPath).Compile(bytes, 1);
            _attemptedHash = Convert.ToHexString(SHA256.HashData(bytes));
            Loaded(_logger, 1);
        }
        catch (Exception ex) when (IsConfigurationFailure(ex))
        {
            throw new InvalidOperationException("Gateway startup configuration invalid. Check rule source, schema and approved destinations.");
        }
        _reload = _options.ReloadOnChange ? ReloadAsync() : Task.CompletedTask;
    }

    internal RuleSnapshot Current => Volatile.Read(ref _snapshot);

    private byte[] ReadDocument()
    {
        byte[] bytes;
        if (_options.ProgrammaticRules is not null)
        {
            if (_explicitSerialized is not null)
                throw new InvalidOperationException("Conflicting explicit rule sources.");
            bytes = ReadProgrammatic();
        }
        else if (string.Equals(_options.RuleSource, "Inline", StringComparison.OrdinalIgnoreCase))
            bytes = ReadInline();
        else if (string.Equals(_options.RuleSource, "Configuration", StringComparison.OrdinalIgnoreCase))
        {
            if (_configuration.GetSection("MockProxyGateway:Rules").Exists())
                throw new InvalidOperationException("Conflicting inline and serialized rule sources.");
            var serialized = _configuration.GetSection("MockProxyGateway:RulesJson");
            if (serialized.GetChildren().Any())
                throw new InvalidOperationException("Serialized rules must be a single scalar document.");
            var json = (_hasExplicitSerialized ? _explicitSerialized : serialized.Value) ??
                throw new InvalidOperationException("Complete serialized rule document required.");
            bytes = Encoding.UTF8.GetBytes(json);
        }
        else if (string.Equals(_options.RuleSource, "File", StringComparison.OrdinalIgnoreCase))
        {
            if (_configuration.GetSection("MockProxyGateway:Rules").Exists() ||
                _configuration.GetSection("MockProxyGateway:RulesJson").Exists() || _explicitSerialized is not null)
                throw new InvalidOperationException("Conflicting file and inline/serialized rule sources.");
            var path = Path.GetFullPath(_options.ConfigurationFile, _environment.ContentRootPath);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaximumDocumentBytes)
                throw new InvalidOperationException("Rule document too large.");
            bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
                throw new InvalidOperationException("Rule document changed during read.");
        }
        else
            throw new InvalidOperationException("Unsupported rule source.");
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidOperationException("Rule document too large.");
        return bytes;
    }

    private byte[] ReadProgrammatic()
    {
        try
        {
            return Encoding.UTF8.GetBytes(_options.ProgrammaticRules!().GetRawText());
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // A caller-owned provider can fail for reasons outside the built-in configuration
            // exception types. Never terminate reload or leak provider exception data.
            throw new InvalidOperationException("Programmatic rule provider failed.");
        }
    }

    private byte[] ReadInline()
    {
        const string section = "MockProxyGateway:Rules";
        if (_configuration.GetSection("MockProxyGateway:RulesJson").Exists() || _explicitSerialized is not null ||
            _configuration is not IConfigurationRoot root)
            throw new InvalidOperationException("Conflicting or unsupported inline rule source.");
        var providers = root.Providers.Where(provider =>
            provider.TryGet(section, out _) || provider.GetChildKeys([], section).Any()).ToArray();
        // Do not reconstruct typed JSON from flattened string-valued configuration keys.
        if (providers.Length != 1 || providers[0] is not JsonConfigurationProvider jsonProvider ||
            jsonProvider.Source.FileProvider is null || jsonProvider.Source.Path is null)
            throw new InvalidOperationException("Inline rules require one complete JSON-file provider; use a serialized document for other providers.");
        var info = jsonProvider.Source.FileProvider.GetFileInfo(jsonProvider.Source.Path);
        using var stream = info.CreateReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
        {
            if (buffer.Length + count > MaximumDocumentBytes)
                throw new InvalidOperationException("Inline source too large.");
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 64
        });
        RuleCompiler.ValidateJsonTree(document.RootElement);
        var gateway = FindProperty(document.RootElement, "MockProxyGateway");
        var rules = FindProperty(gateway, "Rules");
        return Encoding.UTF8.GetBytes(rules.GetRawText());
    }

    private static JsonElement FindProperty(JsonElement parent, string key)
    {
        var matching = parent.EnumerateObject().Where(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length != 1)
            throw new InvalidOperationException("Missing or ambiguous inline configuration section.");
        return matching[0].Value;
    }

    private async Task ReloadAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var bytes = ReadDocument();
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (hash == _attemptedHash)
                    {
                        if (_lastFailed)
                        {
                            _lastFailed = false;
                            _rejectedHash = null;
                            Loaded(_logger, Current.Version);
                        }
                        continue;
                    }
                    if (hash == _rejectedHash)
                        continue;
                    _rejectedHash = hash;
                    var next = new RuleCompiler(_options, _environment.ContentRootPath).Compile(bytes, Current.Version + 1);
                    _attemptedHash = hash;
                    _rejectedHash = null;
                    Volatile.Write(ref _snapshot, next);
                    _lastFailed = false;
                    Loaded(_logger, next.Version);
                }
                catch (Exception ex) when (IsConfigurationFailure(ex))
                {
                    // I/O and access failures may recover without changing the rule
                    // document (for example, a fixture mounted after the rules).
                    // Retry at the existing polling cadence; deterministic
                    // validation failures still coalesce by rejected hash.
                    if (ex is IOException or UnauthorizedAccessException)
                        _rejectedHash = null;
                    if (!_lastFailed)
                        ReloadFailed(_logger, Current.Version);
                    _lastFailed = true;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private static bool IsConfigurationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
            ArgumentException or FormatException or NotSupportedException or KeyNotFoundException or OverflowException;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _reload.ConfigureAwait(false);
        _stop.Dispose();
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Gateway configuration version {Version} activated.")]
    private static partial void Loaded(ILogger logger, long version);
    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Gateway configuration reload rejected; retaining version {Version}.")]
    private static partial void ReloadFailed(ILogger logger, long version);
}
