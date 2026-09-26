using System.IO.Pipes;

namespace OmenGamingShell;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\OmenGamingShellSingleton";
    private const string PipeName = "OmenGamingShellActivate";
    private static readonly byte[] ActivateSignal = { 1 };
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listener;
    private bool _disposed;

    public bool IsPrimary { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    public static bool SignalPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);
            client.Write(ActivateSignal, 0, ActivateSignal.Length);
            client.Flush();
            return true;
        }
        catch { return false; }
    }

    public void Listen(Action onActivate)
    {
        if (!IsPrimary || _listener is not null) return;
        var cancellation = new CancellationTokenSource();
        _listener = cancellation;
        var thread = new Thread(() => AcceptLoop(onActivate, cancellation.Token))
        {
            IsBackground = true, Name = "OmenGamingShell activation"
        };
        thread.Start();
    }

    private void AcceptLoop(Action onActivate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                if (token.IsCancellationRequested) return;
                var buffer = new byte[1];
                if (server.Read(buffer, 0, 1) == 1) onActivate();
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener?.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        try
        {
            if (IsPrimary) _mutex.ReleaseMutex();
        }
        catch { }
        _mutex.Dispose();
    }
}
