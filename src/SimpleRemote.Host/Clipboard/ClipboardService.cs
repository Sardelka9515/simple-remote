using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SimpleRemote.Clipboard;

/// <summary>
/// Two-way text clipboard sync.
///
/// The clipboard is STA-only, so this owns a dedicated STA thread with its own message pump and a
/// message-only window. Two reasons it does not simply borrow the UI thread: a modal dialog there
/// would block every clipboard operation, and owning the pump lets us subscribe to
/// WM_CLIPBOARDUPDATE for genuine change notifications instead of polling once a second.
/// </summary>
public sealed partial class ClipboardService : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int WmAppWork = 0x8000 + 1;
    private const int WmQuit = 0x0012;
    private const int HwndMessage = -3;

    /// <summary>Guards against syncing a clipboard so large it would stall the socket.</summary>
    public const int MaxTextLength = 64 * 1024;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private MessageWindow? _window;
    private volatile bool _disposed;

    /// <summary>Last value we saw or set, used to suppress echoing our own writes back to the phone.</summary>
    private string? _lastKnown;

    public event Action<string>? ClipboardChanged;

    public void Start()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "SimpleRemote.Clipboard",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Block briefly so a Push() immediately after Start() is not silently dropped.
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void ThreadMain()
    {
        _window = new MessageWindow(this);
        _window.CreateHandle(new CreateParams { Parent = HwndMessage });

        NativeMethods.AddClipboardFormatListener(_window.Handle);

        // Seed the baseline so the first real change is detected as a change.
        _lastKnown = ReadCore();
        _ready.Set();

        Application.Run(new ApplicationContext());

        NativeMethods.RemoveClipboardFormatListener(_window.Handle);
        _window.DestroyHandle();
    }

    /// <summary>Writes text to the PC clipboard. Safe to call from any thread.</summary>
    public void Push(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];

        Post(() =>
        {
            // Record before writing: the resulting WM_CLIPBOARDUPDATE must not be echoed back to
            // the device that just sent it.
            _lastKnown = text;
            Retry(() => System.Windows.Forms.Clipboard.SetText(text));
        });
    }

    /// <summary>Reads the current PC clipboard text. Safe to call from any thread.</summary>
    public Task<string?> ReadAsync()
    {
        if (_disposed) return Task.FromResult<string?>(null);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult(ReadCore()));

        // Never let a wedged clipboard owner hang a request forever.
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(
            t => t.IsCompletedSuccessfully ? t.Result : null,
            TaskScheduler.Default);
    }

    private void OnClipboardUpdate()
    {
        var text = ReadCore();
        if (text is null || text == _lastKnown) return;

        _lastKnown = text;
        ClipboardChanged?.Invoke(text);
    }

    private static string? ReadCore()
    {
        string? result = null;
        Retry(() =>
        {
            if (!System.Windows.Forms.Clipboard.ContainsText()) return;
            var text = System.Windows.Forms.Clipboard.GetText();
            result = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        });
        return result;
    }

    /// <summary>
    /// The clipboard is a single global resource with no locking discipline; any app can hold it
    /// open for a few milliseconds. A short retry turns a routine collision into a non-event.
    /// </summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException)
            {
                return;
            }
        }
    }

    private void Post(Action action)
    {
        _work.Enqueue(action);

        var handle = _window?.Handle ?? nint.Zero;
        if (handle != nint.Zero) NativeMethods.PostMessage(handle, WmAppWork, nint.Zero, nint.Zero);
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var handle = _window?.Handle ?? nint.Zero;
        if (handle != nint.Zero) NativeMethods.PostMessage(handle, WmQuit, nint.Zero, nint.Zero);

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private sealed class MessageWindow(ClipboardService owner) : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WmClipboardUpdate:
                    owner.OnClipboardUpdate();
                    break;

                case WmAppWork:
                    owner.DrainWork();
                    break;

                case WmQuit:
                    Application.ExitThread();
                    break;
            }

            base.WndProc(ref m);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AddClipboardFormatListener(nint hwnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool RemoveClipboardFormatListener(nint hwnd);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    }
}
