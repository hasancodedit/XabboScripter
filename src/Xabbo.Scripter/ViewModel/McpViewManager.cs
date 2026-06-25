using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;

using MaterialDesignThemes.Wpf;

using Xabbo.Scripter.Configuration;
using Xabbo.Scripter.Mcp.Integration;
using Xabbo.Scripter.Mcp.Server;

namespace Xabbo.Scripter.ViewModel;

public class McpViewManager : ObservableObject
{
    private readonly McpServer _server;
    private readonly McpConfig _config;
    private readonly McpClientConfigurator _configurator;
    private readonly ISnackbarMessageQueue _snackbar;

    public McpServer Server => _server;

    public string Endpoint => _config.Endpoint;
    public string AuthToken => _config.AuthToken;

    public IReadOnlyList<McpClientTarget> Clients => _configurator.Targets();

    public bool Enabled
    {
        get => _config.Enabled;
        set { if (_config.Enabled == value) return; _config.Enabled = value; _config.Save(); RaisePropertyChanged(); }
    }

    public bool StartOnLaunch
    {
        get => _config.StartOnLaunch;
        set { if (_config.StartOnLaunch == value) return; _config.StartOnLaunch = value; _config.Save(); RaisePropertyChanged(); }
    }

    public int Port
    {
        get => _config.Port;
        set
        {
            if (_config.Port == value || value <= 0 || value > 65535) return;
            _config.Port = value;
            _config.Save();
            RaisePropertyChanged();
            RaiseConnectionInfo();
        }
    }

    public bool RequireAuthToken
    {
        get => _config.RequireAuthToken;
        set
        {
            if (_config.RequireAuthToken == value) return;
            _config.RequireAuthToken = value;
            _config.Save();
            RaisePropertyChanged();
            RaiseConnectionInfo();
        }
    }

    public bool AllowExecute
    {
        get => _config.AllowExecute;
        set { if (_config.AllowExecute == value) return; _config.AllowExecute = value; _config.Save(); RaisePropertyChanged(); }
    }

    public bool AllowFileWrite
    {
        get => _config.AllowFileWrite;
        set { if (_config.AllowFileWrite == value) return; _config.AllowFileWrite = value; _config.Save(); RaisePropertyChanged(); }
    }

    public bool AllowEditor
    {
        get => _config.AllowEditor;
        set { if (_config.AllowEditor == value) return; _config.AllowEditor = value; _config.Save(); RaisePropertyChanged(); }
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand RegenerateTokenCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CopyClientCommand { get; }

    public McpViewManager(
        McpServer server,
        McpConfig config,
        McpClientConfigurator configurator,
        ISnackbarMessageQueue snackbar)
    {
        _server = server;
        _config = config;
        _configurator = configurator;
        _snackbar = snackbar;

        _server.PropertyChanged += OnServerPropertyChanged;

        StartCommand = new RelayCommand(async () => await _server.StartServerAsync());
        StopCommand = new RelayCommand(async () => await _server.StopServerAsync());
        RestartCommand = new RelayCommand(async () => await _server.RestartServerAsync());

        RegenerateTokenCommand = new RelayCommand(() =>
        {
            _config.RegenerateToken();
            RaiseConnectionInfo();
            _snackbar.Enqueue("A new authentication token was generated. Reconnect your clients.");
        });

        CopyCommand = new RelayCommand<string>(text =>
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); _snackbar.Enqueue("Copied to clipboard."); }
            catch { _snackbar.Enqueue("Failed to copy to clipboard."); }
        });

        CopyClientCommand = new RelayCommand<McpClientTarget>(target =>
        {
            if (target is null || string.IsNullOrEmpty(target.CopyText)) return;
            try { Clipboard.SetText(target.CopyText); _snackbar.Enqueue($"{target.Name}: copied to clipboard."); }
            catch { _snackbar.Enqueue("Failed to copy to clipboard."); }
        });
    }

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(McpServer.Endpoint) || e.PropertyName == nameof(McpServer.IsRunning))
            RaiseConnectionInfo();
    }

    private void RaiseConnectionInfo()
    {
        RaisePropertyChanged(nameof(Endpoint));
        RaisePropertyChanged(nameof(AuthToken));
        RaisePropertyChanged(nameof(Clients));
    }
}
