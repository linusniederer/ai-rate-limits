using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace AiRateLimits.Providers.Copilot;

/// <summary>
/// GitHub device-flow login for Copilot usage. On success the OAuth token and GitHub login are
/// stored in Windows Credential Manager. Requires user interaction (entering the device code).
/// </summary>
public static class CopilotLogin
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed record DeviceCode(
        string UserCode,
        string VerificationUri,
        string? VerificationUriComplete,
        string DeviceCodeValue,
        int IntervalSeconds,
        int ExpiresInSeconds);

    public sealed record Result(bool Success, string? Login, string? Error);

    /// <summary>
    /// Runs the full device flow. <paramref name="onCodeReady"/> is invoked once the device code is
    /// available so the caller can display it, copy it, and open the browser.
    /// </summary>
    public static async Task<Result> RunAsync(
        string? enterpriseHost,
        Action<DeviceCode> onCodeReady,
        CancellationToken cancellationToken)
    {
        var webHost = CopilotHosts.WebHost(enterpriseHost);
        var apiHost = CopilotHosts.ApiHost(enterpriseHost);

        try
        {
            var device = await RequestDeviceCodeAsync(webHost, cancellationToken).ConfigureAwait(false);
            onCodeReady(device);

            var token = await PollForTokenAsync(webHost, device, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return new Result(false, null, "Login timed out or was denied.");
            }

            var login = await ValidateAsync(apiHost, token, cancellationToken).ConfigureAwait(false);
            if (login is null)
            {
                return new Result(false, null, "Token validation failed.");
            }

            WindowsCredential.Write(CopilotHosts.CredentialTarget, login, token);
            return new Result(true, login, null);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, null, "Login cancelled.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Copilot device-flow login failed");
            return new Result(false, null, ex.Message);
        }
    }

    private static async Task<DeviceCode> RequestDeviceCodeAsync(string webHost, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{webHost}/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = CopilotHosts.OAuthClientId,
                ["scope"] = CopilotHosts.Scopes
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;

        return new DeviceCode(
            UserCode: root.GetProperty("user_code").GetString() ?? string.Empty,
            VerificationUri: root.GetProperty("verification_uri").GetString() ?? $"https://{webHost}/login/device",
            VerificationUriComplete: root.TryGetProperty("verification_uri_complete", out var vc) ? vc.GetString() : null,
            DeviceCodeValue: root.GetProperty("device_code").GetString() ?? string.Empty,
            IntervalSeconds: root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var i) ? i : 5,
            ExpiresInSeconds: root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var e) ? e : 900);
    }

    private static async Task<string?> PollForTokenAsync(string webHost, DeviceCode device, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, device.IntervalSeconds));

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://{webHost}/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = CopilotHosts.OAuthClientId,
                    ["device_code"] = device.DeviceCodeValue,
                    ["grant_type"] = GrantType
                })
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var token) && token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }

            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            switch (error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                default:
                    // expired_token, access_denied, or anything unexpected: stop.
                    return null;
            }
        }

        return null;
    }

    private static async Task<string?> ValidateAsync(string apiHost, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{apiHost}/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AiRateLimits");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }
}
