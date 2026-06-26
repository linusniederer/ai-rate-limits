using System.Diagnostics;
using System.IO;
using System.Windows;
using AiRateLimits.Models;
using AiRateLimits.Providers;
using AiRateLimits.Providers.Copilot;
using AiRateLimits.Services;
using Serilog;
using WinForms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace AiRateLimits;

public partial class App : System.Windows.Application
{
    private SingleInstance? _singleInstance;
    private RateLimitMonitor? _monitor;
    private WinForms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private SettingsStore? _settingsStore;
    private bool _copilotLoginInProgress;
    private LimitHealth? _lastNotifiedHealth;
    private bool _belowThresholdNotified;

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
            new ClaudeCodeRateLimitProvider(),
            new JetBrainsRateLimitProvider(settingsStore),
            new CopilotRateLimitProvider(settingsStore)
        };
        _settingsStore = settingsStore;

        _monitor = new RateLimitMonitor(providers, settingsStore);
        _monitor.Updated += OnMonitorUpdated;

        _mainWindow = new MainWindow(_monitor, settingsStore, StartCopilotLoginAsync, ExitApp);

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
        menu.Items.Add("GitHub Copilot Login…", null, async (_, _) => await StartCopilotLoginAsync());
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

        // Tray reflects the worst across all found providers, matching the auto-display model.
        var result = _monitor.AggregateHealth();
        Dispatcher.Invoke(() =>
        {
            var old = _trayIcon.Icon;
            _trayIcon.Icon = TrayIconFactory.Create(result.Health);
            old?.Dispose();

            MaybeNotify(result, snapshots);
        });
    }

    /// <summary>
    /// Shows a tray balloon when the aggregate health changes between known states (and once for
    /// the first known state). Unknown is never notified — it is only conveyed via tray color/UI.
    /// </summary>
    private void MaybeNotify(
        HealthCalculator.HealthResult result,
        IReadOnlyDictionary<string, VendorRateLimitSnapshot> snapshots)
    {
        if (_trayIcon is null)
        {
            return;
        }

        MaybeNotifyBelowThreshold(result, snapshots);

        if (result.Health == LimitHealth.Unknown || result.Health == _lastNotifiedHealth)
        {
            return;
        }

        _lastNotifiedHealth = result.Health;

        string text;
        if (result.Health == LimitHealth.Healthy)
        {
            text = "All limits are healthy.";
        }
        else if (result.WorstBucket is { } bucket)
        {
            var provider = ProviderNameFor(snapshots, bucket);
            text = $"{provider} {bucket.Name}: {bucket.RemainingPercent:0.#}% available.";

            if (bucket.ResetAt is { } reset)
            {
                var remaining = reset - DateTimeOffset.Now;
                if (remaining > TimeSpan.Zero)
                {
                    text += remaining.TotalHours >= 1
                        ? $" Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m."
                        : $" Resets in {remaining.Minutes}m.";
                }
            }
        }
        else
        {
            text = $"Status is now {result.Health}.";
        }

        var icon = result.Health switch
        {
            LimitHealth.Critical => WinForms.ToolTipIcon.Error,
            LimitHealth.Warning => WinForms.ToolTipIcon.Warning,
            _ => WinForms.ToolTipIcon.Info
        };

        _trayIcon.ShowBalloonTip(5000, $"AI Rate Limits — {result.Health}", text, icon);
    }

    /// <summary>
    /// Optional opt-in alert: when the worst health-affecting bucket's remaining capacity drops
    /// below the configured threshold, notify once until it recovers above the threshold.
    /// </summary>
    private void MaybeNotifyBelowThreshold(
        HealthCalculator.HealthResult result,
        IReadOnlyDictionary<string, VendorRateLimitSnapshot> snapshots)
    {
        var threshold = _settingsStore?.Load().NotifyBelowPercent;
        if (threshold is not { } limit || result.WorstBucket is not { } bucket)
        {
            return;
        }

        if (bucket.RemainingPercent >= limit)
        {
            _belowThresholdNotified = false;
            return;
        }

        if (_belowThresholdNotified)
        {
            return;
        }

        _belowThresholdNotified = true;
        var provider = ProviderNameFor(snapshots, bucket);
        _trayIcon!.ShowBalloonTip(
            5000,
            "AI Rate Limits — Low",
            $"{provider} {bucket.Name}: {bucket.RemainingPercent:0.#}% available (below {limit}%).",
            WinForms.ToolTipIcon.Warning);
    }

    private static string ProviderNameFor(
        IReadOnlyDictionary<string, VendorRateLimitSnapshot> snapshots, Models.RateLimitBucket bucket)
    {
        foreach (var snapshot in snapshots.Values)
        {
            if (snapshot.Buckets.Contains(bucket))
            {
                return snapshot.DisplayName;
            }
        }

        return "AI";
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

    private async Task StartCopilotLoginAsync()
    {
        if (_copilotLoginInProgress)
        {
            return;
        }

        _copilotLoginInProgress = true;
        try
        {
            var enterpriseHost = _settingsStore?.Load().CopilotEnterpriseHost;

            void OnCodeReady(CopilotLogin.DeviceCode code)
            {
                // Non-blocking so device-code polling can run while the dialog is open.
                Dispatcher.BeginInvoke(() =>
                {
                    try { Clipboard.SetText(code.UserCode); } catch { /* clipboard may be busy */ }

                    var url = code.VerificationUriComplete ?? code.VerificationUri;
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Warning(ex, "Could not open browser for Copilot login"); }

                    MessageBox.Show(
                        $"Enter this code in your browser to authorize GitHub Copilot:\n\n" +
                        $"{code.UserCode}\n\n(copied to clipboard)\n\n{url}",
                        "GitHub Copilot Login",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            }

            var result = await CopilotLogin.RunAsync(enterpriseHost, OnCodeReady, CancellationToken.None);

            await Dispatcher.InvokeAsync(() =>
            {
                if (result.Success)
                {
                    MessageBox.Show(
                        $"Signed in as {result.Login}.",
                        "GitHub Copilot Login", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"Login failed: {result.Error}",
                        "GitHub Copilot Login", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });

            if (result.Success && _monitor is not null)
            {
                await _monitor.RefreshAsync();
            }
        }
        finally
        {
            _copilotLoginInProgress = false;
        }
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
