using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace OmenGamingShell;

public enum ControllerCommand { Up, Down, Left, Right, Accept, Back, PreviousTab, NextTab, PreviousPage, NextPage, Settings, Power }

public sealed class ControllerInput : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private RawState _previous;
    private ControllerCommand? _heldDirection;
    private DateTime _nextRepeat;
    private uint? _xInputIndex;
    private ControllerProfile _profile = new();
    private DateTime _acceptPressedAt;
    private bool _acceptLongPressFired;

    public event Action<ControllerCommand>? Command;
    public event Action? GuidePressed;
    public event Action? AcceptHeld;
    public event Action? StateChanged;
    public bool IsEnabled { get; set; } = true;
    public bool IsConnected { get; private set; }
    public string ConnectedControllerName { get; private set; } = "No controller connected";
    public int LeftXPercent { get; private set; }
    public int LeftYPercent { get; private set; }
    public string LastInput { get; private set; } = "Waiting for input";

    public ControllerInput()
    {
        _timer.Tick += Poll;
        _timer.Start();
    }

    public void ApplyProfile(ControllerProfile profile) => _profile = profile;

    public void TestVibration()
    {
        // XInputSetState raises a hardware access violation (uncatchable corrupted-state
        // exception) with some OEM XInput filter drivers, killing the whole shell.
        // Vibration therefore stays disabled until a safe backend replaces raw XInput.
    }

    private void Poll(object? sender, EventArgs e)
    {
        try
        {
            if (GetForegroundWindow() != _shellHandle) return;
            PollCore();
        }
        catch (Exception exception)
        {
            LastInput = $"Input error: {exception.Message}";
        }
    }

    public void SetShellHandle(IntPtr handle) => _shellHandle = handle;

    private IntPtr _shellHandle;

    private void PollCore()
    {
        var current = ReadXInput() ?? ReadJoystick() ?? default;
        var connectionChanged = current.Connected != IsConnected;
        var stateChanged = current.Buttons != _previous.Buttons || current.X != _previous.X || current.Y != _previous.Y;
        IsConnected = current.Connected;
        ConnectedControllerName = current.Name ?? "No controller connected";
        LeftXPercent = current.X;
        LeftYPercent = current.Y;
        if (connectionChanged || stateChanged)
            StateChanged?.Invoke();
        if (!IsEnabled) { _previous = current; return; }

        EmitEdge(current.Up, _previous.Up, ControllerCommand.Up, "D-pad / Stick Up");
        EmitEdge(current.Down, _previous.Down, ControllerCommand.Down, "D-pad / Stick Down");
        EmitEdge(current.Left, _previous.Left, ControllerCommand.Left, "D-pad / Stick Left");
        EmitEdge(current.Right, _previous.Right, ControllerCommand.Right, "D-pad / Stick Right");
        EmitEdge(current.LeftTrigger, _previous.LeftTrigger, ControllerCommand.PreviousPage, "Left Trigger");
        EmitEdge(current.RightTrigger, _previous.RightTrigger, ControllerCommand.NextPage, "Right Trigger");
        foreach (var binding in _profile.Bindings.Where(binding => binding.Key != ControllerCommand.Accept))
            EmitEdge(current.IsPressed(binding.Value), _previous.IsPressed(binding.Value), binding.Key, binding.Value.ToString());
        var acceptButton = _profile.Bindings.GetValueOrDefault(ControllerCommand.Accept, ControllerButton.A);
        var acceptPressed = current.IsPressed(acceptButton);
        var acceptWasPressed = _previous.IsPressed(acceptButton);
        if (acceptPressed && !acceptWasPressed)
        {
            _acceptPressedAt = DateTime.UtcNow;
            _acceptLongPressFired = false;
        }
        else if (acceptPressed && !_acceptLongPressFired && DateTime.UtcNow - _acceptPressedAt >= TimeSpan.FromMilliseconds(650))
        {
            _acceptLongPressFired = true;
            LastInput = $"Hold {acceptButton}";
            AcceptHeld?.Invoke();
            if (_profile.VibrationEnabled) TestVibration();
        }
        else if (!acceptPressed && acceptWasPressed && !_acceptLongPressFired)
        {
            LastInput = acceptButton.ToString();
            Command?.Invoke(ControllerCommand.Accept);
            if (_profile.VibrationEnabled) TestVibration();
        }
        if (current.Guide && !_previous.Guide)
        {
            LastInput = "Guide";
            GuidePressed?.Invoke();
        }

        ControllerCommand? direction = current.Up ? ControllerCommand.Up : current.Down ? ControllerCommand.Down :
            current.Left ? ControllerCommand.Left : current.Right ? ControllerCommand.Right : null;
        if (direction != _heldDirection) { _heldDirection = direction; _nextRepeat = DateTime.UtcNow.AddMilliseconds(220); }
        else if (direction is not null && DateTime.UtcNow >= _nextRepeat)
        {
            Command?.Invoke(direction.Value);
            _nextRepeat = DateTime.UtcNow.AddMilliseconds(65);
        }
        _previous = current;
    }

    private void EmitEdge(bool value, bool previous, ControllerCommand command, string label)
    {
        if (!value || previous) return;
        LastInput = label;
        Command?.Invoke(command);
        if (_profile.VibrationEnabled && command == ControllerCommand.Accept) TestVibration();
    }

    private RawState? ReadXInput()
    {
        if (_xInputIndex is { } cached && XInputGetState(cached, out var cachedState) == 0)
        {
            try { if (XInputGetStateEx(cached, out var extended) == 0) cachedState = extended; }
            catch (EntryPointNotFoundException) { }
            return BuildXInputState(cached, cachedState);
        }
        for (uint index = 0; index < 4; index++)
        {
            if (XInputGetState(index, out var state) != 0) continue;
            try { if (XInputGetStateEx(index, out var extended) == 0) state = extended; }
            catch (EntryPointNotFoundException) { }
            _xInputIndex = index;
            return BuildXInputState(index, state);
        }
        _xInputIndex = null;
        return null;
    }

    private RawState BuildXInputState(uint index, XInputState state)
    {
        var threshold = 32767 * Math.Clamp(_profile.DeadZonePercent, 5, 60) / 100;
        var b = state.Gamepad.Buttons;
        return new RawState(true, $"Xbox / XInput Controller {index + 1}", true,
            (b & 1) != 0 || state.Gamepad.LeftThumbY > threshold,
            (b & 2) != 0 || state.Gamepad.LeftThumbY < -threshold,
            (b & 4) != 0 || state.Gamepad.LeftThumbX < -threshold,
            (b & 8) != 0 || state.Gamepad.LeftThumbX > threshold,
            (int)Math.Round(state.Gamepad.LeftThumbX / 32767d * 100),
            (int)Math.Round(state.Gamepad.LeftThumbY / 32767d * 100), b, (b & 0x0400) != 0,
            state.Gamepad.LeftTrigger >= 30, state.Gamepad.RightTrigger >= 30);
    }

    private RawState? ReadJoystick()
    {
        for (uint id = 0; id < joyGetNumDevs(); id++)
        {
            var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = 0xFF };
            if (joyGetPosEx(id, ref info) != 0) continue;
            var x = (int)Math.Round((info.X - 32767.5) / 32767.5 * 100);
            var y = (int)Math.Round((32767.5 - info.Y) / 32767.5 * 100);
            var threshold = Math.Clamp(_profile.DeadZonePercent, 5, 60);
            var pov = info.Pov;
            var capabilities = new JoyCaps();
            var controllerName = joyGetDevCaps(id, ref capabilities, (uint)Marshal.SizeOf<JoyCaps>()) == 0 &&
                                 !string.IsNullOrWhiteSpace(capabilities.ProductName)
                ? capabilities.ProductName
                : $"Windows HID Controller {id + 1}";
            return new RawState(true, controllerName, false,
                pov is >= 31500 and <= 35999 || pov is >= 0 and <= 4500 || y > threshold,
                pov is >= 13500 and <= 22500 || y < -threshold,
                pov is >= 22500 and <= 31500 || x < -threshold,
                pov is >= 4500 and <= 13500 || x > threshold, x, y, info.Buttons,
                (info.Buttons & 0x1000) != 0, false, false);
        }
        return null;
    }

    public void Dispose() { _timer.Stop(); }

    private readonly record struct RawState(bool Connected, string? Name, bool NativeXInput, bool Up, bool Down, bool Left, bool Right,
        int X, int Y, uint Buttons, bool Guide, bool LeftTrigger, bool RightTrigger)
    {
        public bool IsPressed(ControllerButton button) => button switch
        {
            ControllerButton.A => (Buttons & (NativeXInput ? 0x1000 : 0x1)) != 0,
            ControllerButton.B => (Buttons & (NativeXInput ? 0x2000 : 0x2)) != 0,
            ControllerButton.X => (Buttons & (NativeXInput ? 0x4000 : 0x4)) != 0,
            ControllerButton.Y => (Buttons & (NativeXInput ? 0x8000 : 0x8)) != 0,
            ControllerButton.LeftShoulder => (Buttons & (NativeXInput ? 0x0100 : 0x10)) != 0,
            ControllerButton.RightShoulder => (Buttons & (NativeXInput ? 0x0200 : 0x20)) != 0,
            ControllerButton.Menu => (Buttons & (NativeXInput ? 0x0010 : 0x200)) != 0,
            ControllerButton.View => (Buttons & (NativeXInput ? 0x0020 : 0x100)) != 0,
            _ => false
        };
    }

    [StructLayout(LayoutKind.Sequential)] private struct XInputState { public uint Packet; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftThumbX, LeftThumbY, RightThumbX, RightThumbY; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputVibration { public ushort LeftMotor, RightMotor; }
    [StructLayout(LayoutKind.Sequential)] private struct JoyInfoEx { public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort ManufacturerId, ProductId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
        public uint XMin, XMax, YMin, YMax, ZMin, ZMax, ButtonCount, PeriodMin, PeriodMax;
        public uint RMin, RMax, UMin, UMax, VMin, VMax, Capabilities, MaxAxes, Axes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemDriver;
    }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint userIndex, out XInputState state);
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint userIndex, out XInputState state);
    [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint joystickId, ref JoyInfoEx info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint joyGetDevCaps(uint joystickId, ref JoyCaps capabilities, uint size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}




