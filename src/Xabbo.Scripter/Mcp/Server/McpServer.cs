using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using GalaSoft.MvvmLight;

using Xabbo.Scripter.Configuration;

namespace Xabbo.Scripter.Mcp.Server;

public sealed class McpServer : ObservableObject, IHostedService, IMcpActivitySink
{
    private readonly McpConfig _config;
    private readonly McpHttpHandler _handler;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IHost? _webHost;

    public McpConfig Config => _config;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
                RaisePropertyChanged(nameof(StatusText));
        }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (Set(ref _lastError, value))
                RaisePropertyChanged(nameof(StatusText));
        }
    }

    private int _requestCount;
    public int RequestCount => _requestCount;

    private int _sessionCount;
    public int SessionCount => _sessionCount;

    public string Endpoint => _config.Endpoint;

    public string StatusText =>
        IsRunning ? $"running on {Endpoint}" :
        LastError is not null ? $"stopped — {LastError}" :
        "stopped";

    public McpServer(McpConfig config, McpDispatcher dispatcher, ILogger<McpServer> logger)
    {
        _config = config;
        _logger = logger;
        _handler = new McpHttpHandler(dispatcher, config, this);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.Enabled && _config.StartOnLaunch)
            await StartServerAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync().ConfigureAwait(false);
    }

    public async Task StartServerAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_webHost is not null)
                return;

            LastError = null;

            int port = FindFreePort(_config.Port);
            _config.ActivePort = port;
            RaisePropertyChanged(nameof(Endpoint));

            IHost host = new HostBuilder()
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseSetting(WebHostDefaults.ApplicationKey, typeof(McpServer).Assembly.GetName().Name);
                    web.UseSetting("AllowedHosts", "*");
                    web.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
                    web.Configure(app => app.Run(_handler.HandleAsync));
                })
                .ConfigureLogging(logging => logging.ClearProviders())
                .Build();

            try
            {
                await host.StartAsync().ConfigureAwait(false);
            }
            catch
            {
                host.Dispose();
                throw;
            }

            _webHost = host;
            IsRunning = true;

            _logger.LogInformation("MCP server listening on {endpoint}", Endpoint);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;

            _logger.LogError(ex, "Failed to start MCP server: {message}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopServerAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_webHost is null)
                return;

            try { await _webHost.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            _webHost.Dispose();
            _webHost = null;

            IsRunning = false;
            _config.ActivePort = 0;
            RaisePropertyChanged(nameof(Endpoint));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartServerAsync()
    {
        await StopServerAsync().ConfigureAwait(false);
        await StartServerAsync().ConfigureAwait(false);
    }

    void IMcpActivitySink.OnRequest(string? method)
    {
        Interlocked.Increment(ref _requestCount);
        RaisePropertyChanged(nameof(RequestCount));
    }

    void IMcpActivitySink.OnSessionOpened(string sessionId)
    {
        Interlocked.Increment(ref _sessionCount);
        RaisePropertyChanged(nameof(SessionCount));
    }

    private static int FindFreePort(int basePort)
    {
        for (int port = basePort; port <= 65535 && port < basePort + 64; port++)
        {
            if (IsPortFree(port))
                return port;
        }

        return basePort;
    }

    private static bool IsPortFree(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
