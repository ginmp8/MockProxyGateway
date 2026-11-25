using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
    private bool _lastFailed;
    private readonly Task _reload;

    public RuleStore(IOptions<GatewayOptions> options, IHostEnvironment environment, IConfiguration configuration, ILogger<RuleStore> logger)
    {
        _options = options.Value;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
        if (_options.AllowLoopbackForTests && !environment.IsDevelopment() &&
            !string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Loopback forwarding is permitted only in Development or Testing.");
        try
        {
            var bytes = ReadDocument();
            _snapshot = new RuleCompiler(_options).Compile(bytes, 1);
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
            bytes = Encoding.UTF8.GetBytes(_options.ProgrammaticRules().GetRawText());
        else if (string.Equals(_options.RuleSource, "Configuration", StringComparison.OrdinalIgnoreCase))
        {
            var json = _configuration["MockProxyGateway:RulesJson"] ?? _options.RulesJson ??
                throw new InvalidOperationException("Complete serialized rule document required.");
            bytes = Encoding.UTF8.GetBytes(json);
        }
        else if (string.Equals(_options.RuleSource, "File", StringComparison.OrdinalIgnoreCase))
        {
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
                        continue;
                    _attemptedHash = hash;
                    var next = new RuleCompiler(_options).Compile(bytes, Current.Version + 1);
                    Volatile.Write(ref _snapshot, next);
                    _lastFailed = false;
                    Loaded(_logger, next.Version);
                }
                catch (Exception ex) when (IsConfigurationFailure(ex))
                {
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
