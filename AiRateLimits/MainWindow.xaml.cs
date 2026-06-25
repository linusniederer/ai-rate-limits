using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiRateLimits.Models;
using AiRateLimits.Providers;
using AiRateLimits.Providers.Copilot;
using AiRateLimits.Services;
using Color = System.Windows.Media.Color;

namespace AiRateLimits;

public partial class MainWindow : Window
{
    private readonly RateLimitMonitor _monitor;
    private readonly Func<Task> _copilotLogin;

    public MainWindow(RateLimitMonitor monitor, Func<Task> copilotLogin)
    {
        InitializeComponent();
        _monitor = monitor;
        _copilotLogin = copilotLogin;
        _monitor.Updated += OnUpdated;
        Render();
    }

    private void OnUpdated(IReadOnlyDictionary<string, VendorRateLimitSnapshot> snapshots) =>
        Dispatcher.Invoke(Render);

    private void Render()
    {
        var aggregate = _monitor.AggregateHealth();
        SummaryText.Text = $"Status: {aggregate.Health}";
        SummaryText.Foreground = new SolidColorBrush(MediaColorFor(aggregate.Health));
        LastUpdateText.Text = $"Last update: {DateTimeOffset.Now:HH:mm:ss}";

        // Auto-display: only providers that actually returned data are shown.
        ProvidersPanel.Children.Clear();
        foreach (var snapshot in _monitor.FoundSnapshots)
        {
            ProvidersPanel.Children.Add(BuildProviderBlock(snapshot));
        }

        if (ProvidersPanel.Children.Count == 0)
        {
            ProvidersPanel.Children.Add(new TextBlock
            {
                Text = "No provider data found yet. Searching…",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
                TextWrapping = TextWrapping.Wrap
            });
        }

        UpdateCopilotStatus();
    }

    private void UpdateCopilotStatus()
    {
        var credential = WindowsCredential.Read(CopilotHosts.CredentialTarget);
        var loggedIn = credential is not null && !string.IsNullOrWhiteSpace(credential.Secret);

        if (loggedIn)
        {
            CopilotStatusText.Text = $"Copilot: signed in as {credential!.UserName}";
            CopilotLoginButton.Content = "Re-login";
        }
        else
        {
            CopilotStatusText.Text = "Copilot: not signed in";
            CopilotLoginButton.Content = "GitHub Copilot Login";
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

    private static UIElement BuildProviderBlock(VendorRateLimitSnapshot snapshot)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        panel.Children.Add(new TextBlock
        {
            Text = snapshot.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });

        if (!string.IsNullOrEmpty(snapshot.Error))
        {
            panel.Children.Add(new TextBlock
            {
                Text = snapshot.Error,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        foreach (var bucket in snapshot.Buckets)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{bucket.Name}: {bucket.RemainingPercent:0.#}% available",
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        return panel;
    }

    private static Color MediaColorFor(LimitHealth health) => health switch
    {
        LimitHealth.Healthy => Color.FromRgb(0x2E, 0xCC, 0x71),
        LimitHealth.Warning => Color.FromRgb(0xE6, 0x7E, 0x22),
        LimitHealth.Critical => Color.FromRgb(0xE7, 0x4C, 0x3C),
        _ => Color.FromRgb(0x95, 0xA5, 0xA6)
    };

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await _monitor.RefreshAsync();

    /// <summary>Closing hides the window; the app only exits from the tray menu.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
