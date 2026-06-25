using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace AiRateLimits.Services;

/// <summary>
/// Per-user single-instance coordination via a named mutex plus a named pipe.
/// A second launch signals the first to activate, then exits. With --minimized it exits silently.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string NameStem = "AiRateLimits_5F68F81D";
    private const byte ActivateByte = 0x01;

    private readonly string _name;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;

    /// <summary>Raised on the first instance when another launch requests activation.</summary>
    public event Action? ActivationRequested;

    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "anonymous";
        _name = $"{NameStem}_{sid.Replace('-', '_')}";
    }

    /// <summary>True if this process became the owning (first) instance.</summary>
    public bool IsFirstInstance { get; private set; }

    /// <summary>
    /// Attempts to acquire ownership. Returns true if this is the first instance.
    /// If not, and <paramref name="signalActivation"/> is true, asks the first instance to activate.
    /// </summary>
    public bool TryAcquire(bool signalActivation)
    {
        _mutex = new Mutex(initiallyOwned: true, $"{_name}_mutex", out var createdNew);
        IsFirstInstance = createdNew;

        if (createdNew)
        {
            StartListener();
            return true;
        }

        if (signalActivation)
        {
            SignalExistingInstance();
        }

        return false;
    }

    private void StartListener()
    {
        _listenerCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_listenerCts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    $"{_name}_pipe", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                var buffer = new byte[1];
                var read = await server.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);
                if (read == 1 && buffer[0] == ActivateByte)
                {
                    ActivationRequested?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep the listener alive across transient pipe failures.
            }
        }
    }

    private void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", $"{_name}_pipe", PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(ActivateByte);
            client.Flush();
        }
        catch
        {
            // If we cannot reach the first instance there is nothing more to do; exit anyway.
        }
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _mutex?.Dispose();
    }
}
