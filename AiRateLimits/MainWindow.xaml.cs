using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AiRateLimits.Models;
using AiRateLimits.Providers;
using AiRateLimits.Providers.Copilot;
using AiRateLimits.Services;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AiRateLimits;

public partial class MainWindow : Window
{
    private readonly RateLimitMonitor _monitor;
    private readonly SettingsStore _settingsStore;
    private readonly Func<Task> _copilotLogin;
    private readonly Action _requestExit;
    private string? _selectedProvider;

    public MainWindow(RateLimitMonitor monitor, SettingsStore settingsStore, Func<Task> copilotLogin,
        Action requestExit)
    {
        InitializeComponent();
        _monitor = monitor;
        _settingsStore = settingsStore;
        _copilotLogin = copilotLogin;
        _requestExit = requestExit;
        _monitor.Updated += OnUpdated;
        Render();
    }

    private void OnUpdated(IReadOnlyDictionary<string, VendorRateLimitSnapshot> snapshots) =>
        Dispatcher.Invoke(Render);

    // ===================== Title bar =====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* not draggable mid-gesture */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // ===================== Settings navigation =====================

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        MainView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Collapsed;
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowMainView();

    private void ShowMainView()
    {
        SettingsView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;
    }

    private void LoadSettingsIntoUi()
    {
        var s = _settingsStore.Load();
        WarningBox.Text = s.WarningUsedPercent.ToString();
        CriticalBox.Text = s.CriticalUsedPercent.ToString();
        RefreshBox.Text = s.RefreshMinutes.ToString();
        CopilotHostBox.Text = s.CopilotEnterpriseHost;
        JetBrainsPathBox.Text = s.JetBrainsIdeBasePath;
        AutostartToggle.IsChecked = AutostartManager.IsEnabled();
        ExitOnCloseToggle.IsChecked = s.ExitOnClose;
        NotifyBelowBox.Text = s.NotifyBelowPercent?.ToString() ?? string.Empty;
        AboutVersionText.Text = $"AI Rate Limits {AppVersion}";
        UpdateCopilotStatus();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = _settingsStore.Load();
        if (int.TryParse(WarningBox.Text, out var warn)) s.WarningUsedPercent = warn;
        if (int.TryParse(CriticalBox.Text, out var crit)) s.CriticalUsedPercent = crit;
        if (int.TryParse(RefreshBox.Text, out var refresh)) s.RefreshMinutes = refresh;
        s.CopilotEnterpriseHost = CopilotHostBox.Text.Trim();
        s.JetBrainsIdeBasePath = JetBrainsPathBox.Text.Trim();
        s.ExitOnClose = ExitOnCloseToggle.IsChecked == true;
        s.NotifyBelowPercent = int.TryParse(NotifyBelowBox.Text, out var below) ? below : null;

        _settingsStore.Save(s); // Normalizes/clamps on save.
        _monitor.ReloadSettings();

        ShowMainView();
        _ = _monitor.RefreshAsync();
    }

    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/linusniederer/ai-rate-limits") { UseShellExecute = true });
        }
        catch
        {
            // Ignore if no browser is available.
        }
    }

    private void AutostartToggle_Click(object sender, RoutedEventArgs e)
    {
        AutostartManager.SetEnabled(AutostartToggle.IsChecked == true);
        AutostartToggle.IsChecked = AutostartManager.IsEnabled();
    }

    /// <summary>
    /// Closing hides to tray by default; with the "exit on close" setting it quits the app instead.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        if (_settingsStore.Load().ExitOnClose)
        {
            _requestExit();
        }
        else
        {
            Hide();
        }
    }

    // ===================== Rendering =====================

    private void Render()
    {
        var found = _monitor.FoundSnapshots;
        var aggregate = _monitor.AggregateHealth();

        RenderStatusHeader(aggregate);
        LastUpdateText.Text = found.Count == 0 ? "" : $"Updated {DateTimeOffset.Now:HH:mm}";

        // Keep the selection valid as providers come and go.
        if (_selectedProvider is null || found.All(s => s.VendorId != _selectedProvider))
        {
            _selectedProvider = found.FirstOrDefault()?.VendorId;
        }

        RenderTabs(found);
        RenderDetail(found.FirstOrDefault(s => s.VendorId == _selectedProvider));
        UpdateCopilotStatus();
    }

    private void RenderStatusHeader(HealthCalculator.HealthResult aggregate)
    {
        StatusDot.Fill = HealthBrush(aggregate.Health);
        StatusText.Text = aggregate.Health switch
        {
            LimitHealth.Healthy => "Healthy",
            LimitHealth.Warning => "Warning",
            LimitHealth.Critical => "Critical",
            _ => "Unknown"
        };

        StatusSubtitle.Text = aggregate.Health switch
        {
            LimitHealth.Unknown => "Searching for provider data…",
            LimitHealth.Healthy => "All limits in range",
            _ when aggregate.WorstBucket is { } b => $"{b.Name}: {b.RemainingPercent:0}% left",
            _ => ""
        };
    }

    private void RenderTabs(IReadOnlyList<VendorRateLimitSnapshot> found)
    {
        TabsPanel.Children.Clear();
        foreach (var snapshot in found)
        {
            TabsPanel.Children.Add(BuildTab(snapshot));
        }
    }

    private UIElement BuildTab(VendorRateLimitSnapshot snapshot)
    {
        var selected = snapshot.VendorId == _selectedProvider;
        var health = HealthCalculator.Evaluate(
            snapshot.Buckets, _monitor.Settings.WarningUsedPercent, _monitor.Settings.CriticalUsedPercent);

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(IconKey(snapshot.VendorId)),
            Fill = selected ? Brush("TextBrush") : Brush("TextSecondaryBrush"),
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = HealthBrush(health.Health)
        });

        var underline = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 7, 0, 0),
            Background = selected ? Brush("AccentBrush") : Brushes.Transparent
        };

        var stack = new StackPanel();
        stack.Children.Add(content);
        stack.Children.Add(underline);

        var tab = new Border
        {
            Background = selected ? Brush("CardBrush") : Brushes.Transparent,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 8, 11, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = snapshot.DisplayName
        };
        tab.MouseLeftButtonUp += (_, _) =>
        {
            _selectedProvider = snapshot.VendorId;
            Render();
        };

        if (!selected)
        {
            tab.MouseEnter += (_, _) => tab.Background = Brush("SurfaceBrush");
            tab.MouseLeave += (_, _) => tab.Background = Brushes.Transparent;
        }

        return tab;
    }

    private void RenderDetail(VendorRateLimitSnapshot? snapshot)
    {
        DetailPanel.Children.Clear();

        if (snapshot is null)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "No provider data found yet. Searching…",
                Foreground = Brush("TextMutedBrush"),
                FontSize = 12.5,
                Margin = new Thickness(2, 18, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        // Provider heading row: name + plan badge + source.
        var heading = new Grid { Margin = new Thickness(2, 0, 2, 10) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(new TextBlock
        {
            Text = snapshot.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(snapshot.PlanType))
        {
            titleStack.Children.Add(BuildBadge(snapshot.PlanType!));
        }

        Grid.SetColumn(titleStack, 0);
        heading.Children.Add(titleStack);

        var source = new TextBlock
        {
            Text = snapshot.Source,
            FontSize = 10.5,
            Foreground = Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(source, 1);
        heading.Children.Add(source);
        DetailPanel.Children.Add(heading);

        foreach (var bucket in snapshot.Buckets)
        {
            DetailPanel.Children.Add(BuildBucketCard(bucket));
        }

        if (snapshot.CostUsage is { HasUsage: true } cost)
        {
            DetailPanel.Children.Add(BuildCostCard(cost));
        }
    }

    private Border BuildCostCard(CostUsageSnapshot cost)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "ESTIMATED COST",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thisMonth = new StackPanel();
        thisMonth.Children.Add(new TextBlock
        {
            Text = $"${cost.MonthCostUsd:0.00}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextBrush")
        });
        thisMonth.Children.Add(new TextBlock
        {
            Text = $"this month · {FormatTokens(cost.MonthTokens)} tokens · {cost.MonthRequests} req",
            FontSize = 10.5,
            Foreground = Brush("TextMutedBrush"),
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(thisMonth, 0);
        row.Children.Add(thisMonth);

        if (cost.PreviousMonth is { } prev)
        {
            var lastMonth = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            lastMonth.Children.Add(new TextBlock
            {
                Text = $"${prev.MonthCostUsd:0.00}",
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = Brush("TextSecondaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            });
            lastMonth.Children.Add(new TextBlock
            {
                Text = "last month",
                FontSize = 10.5,
                Foreground = Brush("TextMutedBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            });
            Grid.SetColumn(lastMonth, 1);
            row.Children.Add(lastMonth);
        }

        panel.Children.Add(row);

        panel.Children.Add(new TextBlock
        {
            Text = $"Estimate from local logs · {cost.Source}",
            FontSize = 10,
            Foreground = Brush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        return new Border
        {
            Background = Brush("CardBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 4, 0, 10),
            Child = panel
        };
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000.0:0.#}M";
        }
        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000.0:0.#}k";
        }
        return tokens.ToString();
    }

    private Border BuildBucketCard(RateLimitBucket bucket)
    {
        var health = HealthCalculator.ClassifyBucket(
            bucket, _monitor.Settings.WarningUsedPercent, _monitor.Settings.CriticalUsedPercent);
        var healthBrush = HealthBrush(bucket.AffectsHealth ? health : LimitHealth.Unknown);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: name + value
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = bucket.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 0);
        top.Children.Add(name);

        var valueText = bucket.ValueText ?? $"{bucket.RemainingPercent:0}% left";
        var value = new TextBlock
        {
            Text = valueText,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = healthBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 1);
        top.Children.Add(value);
        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        // Row 1: progress bar (remaining capacity)
        var bar = new ProgressBar
        {
            Style = (Style)FindResource("BucketBar"),
            Value = bucket.RemainingPercent,
            Foreground = healthBrush,
            Margin = new Thickness(0, 9, 0, 0)
        };
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        // Row 2: window + reset, optional note
        var metaParts = new List<string>();
        if (bucket.Window is { } w)
        {
            metaParts.Add($"{FormatWindow(w)} window");
        }
        if (bucket.ResetAt is { } reset)
        {
            metaParts.Add(FormatReset(reset));
        }

        var metaStack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (metaParts.Count > 0)
        {
            metaStack.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", metaParts),
                FontSize = 11,
                Foreground = Brush("TextMutedBrush")
            });
        }
        if (!string.IsNullOrWhiteSpace(bucket.Note))
        {
            metaStack.Children.Add(new TextBlock
            {
                Text = bucket.Note,
                FontSize = 10.5,
                Foreground = Brush("TextMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, metaParts.Count > 0 ? 3 : 0, 0, 0)
            });
        }
        Grid.SetRow(metaStack, 2);
        grid.Children.Add(metaStack);

        return new Border
        {
            Background = Brush("CardBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
            Opacity = bucket.AffectsHealth ? 1.0 : 0.75
        };
    }

    private Border BuildBadge(string text) => new()
    {
        Background = Brush("TrackBrush"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 2, 7, 3),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = Brush("TextSecondaryBrush")
        }
    };

    // ===================== Copilot login =====================

    private void UpdateCopilotStatus()
    {
        var credential = WindowsCredential.Read(CopilotHosts.CredentialTarget);
        var loggedIn = credential is not null && !string.IsNullOrWhiteSpace(credential.Secret);

        if (loggedIn)
        {
            CopilotLoginLabel.Text = "Re-link";
            CopilotStatusText.Text = $"Copilot · {credential!.UserName}";
        }
        else
        {
            CopilotLoginLabel.Text = "Sign in";
            CopilotStatusText.Text = "GitHub Copilot not linked";
        }
    }

    private async void CopilotLoginButton_Click(object sender, RoutedEventArgs e)
    {
        CopilotLoginButton.IsEnabled = false;
        try
        {
            await _copilotLogin();
        }
        finally
        {
            CopilotLoginButton.IsEnabled = true;
            UpdateCopilotStatus();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await _monitor.RefreshAsync();

    // ===================== Helpers =====================

    private static string IconKey(string vendorId) => vendorId switch
    {
        CodexRateLimitProvider.ProviderId => "IconCodex",
        ClaudeCodeRateLimitProvider.ProviderId => "IconClaude",
        JetBrainsRateLimitProvider.ProviderId => "IconJetBrains",
        CopilotRateLimitProvider.ProviderId => "IconCopilot",
        _ => "IconClaude"
    };

    private SolidColorBrush HealthBrush(LimitHealth health) => health switch
    {
        LimitHealth.Healthy => Brush("HealthyBrush"),
        LimitHealth.Warning => Brush("WarningBrush"),
        LimitHealth.Critical => Brush("CriticalBrush"),
        _ => Brush("UnknownBrush")
    };

    private SolidColorBrush Brush(string key) => (SolidColorBrush)FindResource(key);

    private static string FormatWindow(TimeSpan w)
    {
        if (w.TotalDays >= 1)
        {
            return $"{w.TotalDays:0.#}d";
        }
        if (w.TotalHours >= 1)
        {
            return $"{w.TotalHours:0.#}h";
        }
        return $"{w.TotalMinutes:0}m";
    }

    private static string FormatReset(DateTimeOffset reset)
    {
        var remaining = reset - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "resetting…";
        }
        if (remaining.TotalDays >= 1)
        {
            return $"resets in {remaining.Days}d {remaining.Hours}h";
        }
        if (remaining.TotalHours >= 1)
        {
            return $"resets in {remaining.Hours}h {remaining.Minutes}m";
        }
        return $"resets in {remaining.Minutes}m";
    }
}
