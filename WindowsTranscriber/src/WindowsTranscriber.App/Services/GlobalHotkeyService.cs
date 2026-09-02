using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowsTranscriber.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x5754;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VirtualKeySpace = 0x20;

    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _isRegistered;

    public event Action? Pressed;

    public bool IsRegistered => _isRegistered;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("Unable to attach the global hotkey.");
        _source.AddHook(WindowProcedure);
    }

    public bool SetEnabled(bool enabled)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!enabled)
        {
            Unregister();
            return true;
        }

        if (_isRegistered)
        {
            return true;
        }

        _isRegistered = RegisterHotKey(
            _windowHandle,
            HotkeyId,
            ModControl | ModShift,
            VirtualKeySpace);
        return _isRegistered;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = IntPtr.Zero;
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WmHotkey && wordParameter.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _isRegistered = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
