using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PortableVoiceInput.Core;

namespace PortableVoiceInput;

internal sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x6147;
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private uint _modifiers, _key;
    private bool _registered;
    public bool IsRegistered => _registered;
    public event Action? Pressed;
    public GlobalHotkey()
    {
        var parameters = new HwndSourceParameters("PortableVoiceInputHotkey") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }
    public bool Register(uint modifiers, uint key)
    {
        var wasRegistered = _registered; var oldModifiers = _modifiers; var oldKey = _key;
        if (wasRegistered) Native.UnregisterHotKey(_source.Handle, Id);
        _registered = Native.RegisterHotKey(_source.Handle, Id, modifiers | 0x4000, key);
        if (_registered) { _modifiers = modifiers; _key = key; return true; }
        if (wasRegistered) _registered = Native.RegisterHotKey(_source.Handle, Id, oldModifiers | 0x4000, oldKey);
        return false;
    }
    public void Suspend()
    {
        if (!_registered) return;
        Native.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id) { Pressed?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }
    public void Dispose() { if (_registered) Native.UnregisterHotKey(_source.Handle, Id); _source.RemoveHook(WndProc); _source.Dispose(); }
}

internal static class Native
{
    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public static void SetOverlayStyles(Window window, bool clickThrough)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var flags = GetWindowLongPtr64(handle, GwlExStyle).ToInt64() | WsExNoActivate | WsExToolWindow;
        if (clickThrough) flags |= WsExTransparent;
        SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(flags));
    }

    public static void KeepOverlayVisible(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;
        var monitor = MonitorFromRect(ref rect, 2); // Nearest monitor, including disconnected saved positions.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        var current = new ScreenRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var work = new ScreenRectangle(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        var fitted = OverlayPlacement.Fit(current, work);
        if (fitted != current)
            SetWindowPos(handle, IntPtr.Zero, fitted.Left, fitted.Top, fitted.Width, fitted.Height, 0x0010 | 0x0004); // No activation or z-order change.
    }

    public static void TrackOverlayDisplayChanges(Window window)
    {
        void Reposition()
        {
            if (!window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.BeginInvoke(() => { if (window.IsVisible) KeepOverlayVisible(window); });
        }
        EventHandler displayChanged = (_, _) => Reposition();
        System.ComponentModel.PropertyChangedEventHandler parametersChanged = (_, e) =>
        {
            if (e.PropertyName is "WorkArea" or "VirtualScreenWidth" or "VirtualScreenHeight" or "VirtualScreenLeft" or "VirtualScreenTop") Reposition();
        };
        window.Loaded += (_, _) => KeepOverlayVisible(window);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayChanged;
        SystemParameters.StaticPropertyChanged += parametersChanged;
        window.Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displayChanged;
            SystemParameters.StaticPropertyChanged -= parametersChanged;
        };
    }

    /// <returns>Whether Ctrl+V was sent to another application. Clipboard is set in either case.</returns>
    public static bool CopyAndPaste(string text)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { System.Windows.Clipboard.SetText(text); last = null; break; }
            catch (System.Runtime.InteropServices.COMException ex) { last = ex; Thread.Sleep(80); }
        }
        if (last is not null) throw new InvalidOperationException("Could not access the clipboard.", last);
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == Environment.ProcessId) return false;
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort key, bool up) => new() { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
}
