using System.Runtime.InteropServices;

namespace WinAssistant.Services;

public class DoubleKeyDetector : IDisposable
{
    private CancellationTokenSource? _cts;
    private int _targetVk;
    private int _intervalMs = 500;
    private bool _running;

    public event EventHandler? Triggered;
    public bool IsRunning => _running;

    public void Start(Windows.System.VirtualKey key, int intervalMs = 500)
    {
        Stop();
        Log($"Start requested: key={(int)key}, interval={intervalMs}");

        _targetVk = (int)key;
        _intervalMs = intervalMs;
        _running = true;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Thread thread = new(() => PollLoop(token))
        {
            IsBackground = true,
            Name = "DoubleKeyDetector"
        };
        thread.Start();
    }

    public void Stop()
    {
        Log("Stop requested");
        _running = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void PollLoop(CancellationToken token)
    {
        Log("PollLoop started");
        bool prevStable = false;
        int stableDown = 0;       // consecutive polls where key is down (debounce)
        DateTime lastPress = DateTime.MinValue;

        try
        {
            while (!token.IsCancellationRequested)
            {
                bool isDown = (GetAsyncKeyState(_targetVk) & 0x8000) != 0;

                if (isDown)
                {
                    // Require 2 consecutive stable polls (~60ms) before treating
                    // this as a real key-down.  GetAsyncKeyState on a background
                    // thread can return transient 1-pulse noise on some systems.
                    if (stableDown < 2)
                        stableDown++;
                }
                else
                {
                    stableDown = 0;
                }

                // Rising edge: key confirmed down after debounce.
                // Track the debounced state separately from raw key state,
                // so the transition from "not debounced" to "debounced" is
                // correctly detected even though isDown stays true.
                bool nowStable = stableDown >= 2;
                if (nowStable && !prevStable)
                {
                    var now = DateTime.UtcNow;
                    if (lastPress != DateTime.MinValue &&
                        (now - lastPress).TotalMilliseconds < _intervalMs)
                    {
                        Log("Double-tap detected!");
                        lastPress = DateTime.MinValue;
                        App.DispatcherQueue.TryEnqueue(() => Triggered?.Invoke(this, EventArgs.Empty));
                    }
                    else
                    {
                        lastPress = now;
                    }
                }

                prevStable = nowStable;
                token.WaitHandle.WaitOne(30);
            }
        }
        catch (Exception ex)
        {
            Log($"PollLoop error: {ex.Message}");
        }

        Log("PollLoop exited");
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "WinAssistant_detector.log");

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Dispose() => Stop();
}
