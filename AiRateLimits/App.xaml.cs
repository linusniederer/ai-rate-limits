using System.IO;
using System.Windows;
using AiRateLimits.Models;
using AiRateLimits.Providers;
using AiRateLimits.Services;
using Serilog;
using WinForms = System.Windows.Forms;

namespace AiRateLimits;

public partial class App : System.Windows.Application
{
    private SingleInstance? _singleInstance;
    private RateLimitMonitor? _monitor;
    private WinForms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startMinimized = e.Args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire(signalActivation: !startMinimized))
        {
            // Another instance owns the mutex. It was (or wasn't, when minimized) asked to activate.
            Shutdown();
            return;
        }

        ConfigureLogging();
        Log.Information("AiRateLimits starting (minimized={Minimized})", startMinimized);

        AutostartManager.MigrateIfNeeded();

        _singleInstance.ActivationRequested += () => Dispatcher.Invoke(ShowMainWindow);

        var settingsStore = new SettingsStore();
        var providers = new IRateLimitProvider[]
        {
            new CodexRateLimitProvider(),
            new JetBrainsRateLimitProvider(),
            new CopilotRateLimitProvider()
        };

        _monitor = new RateLimitMonitor(providers, settingsStore);
        _monitor.Updated += OnMonitorUpdated;

        _mainWindow = new MainWindow(_monitor);

        InitializeTrayIcon();

        _monitor.Start();

        if (!startMinimized)
        {
            ShowMainWindow();
        }
    }

    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiRateLimits", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "airatelimits.log"),
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 1024 * 1024,
                retainedFileCountLimit: 2,
                shared: true)
            .CreateLogger();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = "AI Rate Limits",
            Icon = TrayIconFactory.Create(LimitHealth.Unknown)
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Refresh now", null, async (_, _) =>
        {
            if (_monitor is not null)
            {
                await _monitor.RefreshAsync();
            }
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void OnMonitorUpdated(IReadOnlyDictionary<string, VendorRateLimitSnapshot> snapshots)
    {
        if (_monitor is null || _trayIcon is null)
        {
            return;
        }

        var health = _monitor.StatusProviderHealth().Health;
        Dispatcher.Invoke(() =>
        {
            var old = _trayIcon.Icon;
            _trayIcon.Icon = TrayIconFactory.Create(health);
            old?.Dispose();
        });
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _ = _monitor?.RefreshAsync();
    }

    private void ExitApp()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _monitor?.Dispose();
        _singleInstance?.Dispose();
        Log.CloseAndFlush();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
