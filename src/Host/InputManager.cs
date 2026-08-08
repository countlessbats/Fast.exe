using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.DirectInput;

namespace Fast;

class InputManager : IDisposable
{
    // Callback: (slotIndex, pressed) - pressed=true on key down, false on key up
    private readonly Action<int, bool> _onSlotTriggered;
    private readonly IntPtr _ownerHandle;
    private IntPtr _hookHandle;
    private Native.LowLevelKeyboardProc _hookProc = null!;
    private Thread? _pollThread;
    private volatile bool _running;

    // Current bindings: vkCode -> slotIndex, and controller masks
    private readonly Dictionary<int, int> _keyToSlot = new();
    private readonly List<ControllerSlotBinding> _controllerSlots = new();
    private readonly object _bindLock = new();

    // Tracks which slots have received a press event but not yet a release.
    // Used by the reconcile pass in PollControllers to self-heal from
    // missed key-up events (focus transitions, LL-hook stalls, app-start-
    // with-held-button, thread races). Without reconciliation, a dropped
    // release leaves a hold "stuck" forever — causing the always-on turbo
    // that pressing another trigger only temporarily masks.
    private readonly HashSet<int> _sentPressSlots = new();
    private readonly object _sentPressLock = new();

    // For hotkey capture mode
    private volatile bool _capturing;
    private Action<string>? _captureCallback;

    private IDirectInput8? _directInput;
    private readonly List<DirectInputPad> _directInputPads = new();
    private readonly HashSet<Guid> _directInputIgnoredPads = new();
    private DateTime _lastDirectInputRefresh = DateTime.MinValue;
    private readonly object _rawInputLock = new();
    private ControllerState _rawInputState = new(0, 0, 0, false);
    private DateTime _lastRawInputAt = DateTime.MinValue;
    private HashSet<string> _lastLoggedRawNames = new(StringComparer.OrdinalIgnoreCase);

    struct ControllerSlotBinding
    {
        public ushort ButtonMask;
        public byte TriggerMask;
        public string[] Names;
        public int SlotIndex;
    }

    sealed class DirectInputPad : IDisposable
    {
        public Guid InstanceGuid { get; init; }
        public string Name { get; init; } = "";
        public bool IsSony { get; init; }
        public IDirectInputDevice8 Device { get; init; } = null!;

        public void Dispose()
        {
            try { Device.Unacquire(); } catch { }
            Device.Dispose();
        }
    }

    readonly struct ControllerState
    {
        public readonly ushort Buttons;
        public readonly byte LT;
        public readonly byte RT;
        public readonly bool AnyConnected;
        public readonly HashSet<string> ActiveNames;

        public ControllerState(ushort buttons, byte lt, byte rt, bool anyConnected, HashSet<string>? activeNames = null)
        {
            Buttons = buttons;
            LT = lt;
            RT = rt;
            AnyConnected = anyConnected;
            ActiveNames = activeNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Key name <-> VK mappings (public for hotkey capture display)
    public static readonly Dictionary<int, string> VkToName = new();
    public static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase);

    // Controller button name <-> flag
    public static readonly Dictionary<string, ushort> ButtonNameToFlag = new(StringComparer.OrdinalIgnoreCase);

    static InputManager()
    {
        // F-keys
        for (int i = 1; i <= 12; i++) AddKey($"F{i}", 0x70 + i - 1);
        // Number keys
        for (int i = 0; i <= 9; i++) AddKey($"{i}", 0x30 + i);
        // Letters
        for (char c = 'A'; c <= 'Z'; c++) AddKey($"{c}", (int)c);
        // Numpad
        for (int i = 0; i <= 9; i++) AddKey($"Num{i}", 0x60 + i);
        AddKey("Num*", 0x6A); AddKey("Num+", 0x6B); AddKey("Num-", 0x6D);
        AddKey("Num.", 0x6E); AddKey("Num/", 0x6F);
        // Common
        AddKey("Esc", 0x1B); AddKey("Space", 0x20); AddKey("Tab", 0x09);
        AddKey("Enter", 0x0D); AddKey("Backspace", 0x08); AddKey("Insert", 0x2D);
        AddKey("Delete", 0x2E); AddKey("Home", 0x24); AddKey("End", 0x23);
        AddKey("PgUp", 0x21); AddKey("PgDn", 0x22); AddKey("Pause", 0x13);
        AddKey("~", 0xC0); AddKey("CapsLock", 0x14); AddKey("ScrollLock", 0x91);
        AddKey("[", 0xDB); AddKey("]", 0xDD); AddKey("\\", 0xDC);
        AddKey(";", 0xBA); AddKey("'", 0xDE); AddKey(",", 0xBC);
        AddKey(".", 0xBE); AddKey("/", 0xBF); AddKey("-", 0xBD); AddKey("=", 0xBB);

        // Controller
        ButtonNameToFlag["A"] = Native.XINPUT_GAMEPAD_A;
        ButtonNameToFlag["B"] = Native.XINPUT_GAMEPAD_B;
        ButtonNameToFlag["X"] = Native.XINPUT_GAMEPAD_X;
        ButtonNameToFlag["Y"] = Native.XINPUT_GAMEPAD_Y;
        ButtonNameToFlag["LB"] = Native.XINPUT_GAMEPAD_LEFT_SHOULDER;
        ButtonNameToFlag["RB"] = Native.XINPUT_GAMEPAD_RIGHT_SHOULDER;
        ButtonNameToFlag["Start"] = Native.XINPUT_GAMEPAD_START;
        ButtonNameToFlag["Back"] = Native.XINPUT_GAMEPAD_BACK;
        ButtonNameToFlag["DpadUp"] = Native.XINPUT_GAMEPAD_DPAD_UP;
        ButtonNameToFlag["DpadDown"] = Native.XINPUT_GAMEPAD_DPAD_DOWN;
        ButtonNameToFlag["DpadLeft"] = Native.XINPUT_GAMEPAD_DPAD_LEFT;
        ButtonNameToFlag["DpadRight"] = Native.XINPUT_GAMEPAD_DPAD_RIGHT;
        ButtonNameToFlag["LS"] = Native.XINPUT_GAMEPAD_LEFT_THUMB;
        ButtonNameToFlag["RS"] = Native.XINPUT_GAMEPAD_RIGHT_THUMB;
    }

    static void AddKey(string name, int vk)
    {
        NameToVk[name] = vk;
        VkToName[vk] = name;
    }

    private static string CanonicalControllerName(string name)
    {
        foreach (var key in ButtonNameToFlag.Keys)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        if (name.Equals("LT", StringComparison.OrdinalIgnoreCase)) return "LT";
        if (name.Equals("RT", StringComparison.OrdinalIgnoreCase)) return "RT";
        if (IsDirectInputRawName(name)) return name.ToUpperInvariant();
        return name;
    }

    private static bool IsDirectInputRawName(string name)
    {
        if (!name.StartsWith("DI", StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(name.AsSpan(2), out int button) && button >= 1 && button <= 128;
    }

    public InputManager(Action<int, bool> onSlotTriggered, IntPtr ownerHandle)
    {
        _onSlotTriggered = onSlotTriggered;
        _ownerHandle = ownerHandle;
    }

    public void RebuildBindings(IList<SpeedSlot> slots)
    {
        ReleaseTrackedSlots("bindings changed");

        lock (_bindLock)
        {
            _keyToSlot.Clear();
            _controllerSlots.Clear();

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (!slot.HasBinding || !slot.HasSpeed) continue;

                // Keyboard binding
                if (!string.IsNullOrEmpty(slot.Key) && NameToVk.TryGetValue(slot.Key, out int vk))
                    _keyToSlot[vk] = i;

                // Controller binding
                if (!string.IsNullOrEmpty(slot.Controller))
                {
                    ushort mask = 0;
                    byte trigMask = 0;
                    var names = new List<string>();
                    foreach (var part in slot.Controller.Split('+'))
                    {
                        string p = part.Trim();
                        if (p.Equals("LT", StringComparison.OrdinalIgnoreCase))
                        {
                            trigMask |= 1;
                            names.Add("LT");
                        }
                        else if (p.Equals("RT", StringComparison.OrdinalIgnoreCase))
                        {
                            trigMask |= 2;
                            names.Add("RT");
                        }
                        else if (ButtonNameToFlag.TryGetValue(p, out ushort flag))
                        {
                            mask |= flag;
                            names.Add(CanonicalControllerName(p));
                        }
                        else if (IsDirectInputRawName(p))
                        {
                            names.Add(p.ToUpperInvariant());
                        }
                    }
                    if (mask != 0 || trigMask != 0 || names.Count != 0)
                        _controllerSlots.Add(new ControllerSlotBinding
                            { ButtonMask = mask, TriggerMask = trigMask, Names = names.ToArray(), SlotIndex = i });
                }
            }
        }
    }

    public void Start()
    {
        _hookProc = KeyboardHookCallback;
        _hookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc,
            Native.GetModuleHandleW(null!), 0);

        RegisterRawInput();

        _running = true;
        _pollThread = new Thread(PollControllers) { IsBackground = true, Name = "Controller input" };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        ReleaseTrackedSlots("input stopped");
        if (_hookHandle != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        DisposeDirectInputPads();
        _directInput?.Dispose();
        _directInput = null;
    }

    /// <summary>
    /// Enter capture mode: the next keypress (keyboard or controller button) will be
    /// reported via the callback instead of triggering a speed change.
    /// </summary>
    public void StartCapture(Action<string> callback)
    {
        _captureCallback = callback;
        _capturing = true;
    }

    public void CancelCapture()
    {
        _capturing = false;
        _captureCallback = null;
    }

    public void ProcessRawInput(IntPtr rawInputHandle)
    {
        try
        {
            uint size = 0;
            uint headerSize = (uint)(IntPtr.Size == 8 ? 24 : 16);
            Native.GetRawInputData(rawInputHandle, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint read = Native.GetRawInputData(rawInputHandle, Native.RID_INPUT, buffer, ref size, headerSize);
                if (read == 0 || read == uint.MaxValue) return;

                byte[] data = new byte[size];
                Marshal.Copy(buffer, data, 0, data.Length);
                if (data.Length < headerSize + 8) return;

                uint type = BitConverter.ToUInt32(data, 0);
                if (type != Native.RIM_TYPEHID) return;

                int hidOffset = (int)headerSize;
                uint sizeHid = BitConverter.ToUInt32(data, hidOffset);
                uint count = BitConverter.ToUInt32(data, hidOffset + 4);
                int reportOffset = hidOffset + 8;
                if (sizeHid == 0 || count == 0 || reportOffset >= data.Length) return;

                int reportSize = (int)Math.Min(sizeHid, (uint)(data.Length - reportOffset));
                byte[] report = new byte[reportSize];
                Array.Copy(data, reportOffset, report, 0, reportSize);

                if (!TryParsePlayStationReport(report, out var state))
                    return;

                lock (_rawInputLock)
                {
                    _rawInputState = state;
                    _lastRawInputAt = DateTime.UtcNow;
                }

                LogRawInputChange(state.ActiveNames);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"RawInput failed: {ex.Message}");
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)kbd.vkCode;
            int msg = (int)wParam;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

            // Capture mode: report key name and consume
            if (_capturing && down)
            {
                if (VkToName.TryGetValue(vk, out string? name))
                {
                    _capturing = false;
                    _captureCallback?.Invoke(name);
                    _captureCallback = null;
                }
                return (IntPtr)1; // consume the key
            }

            lock (_bindLock)
            {
                if (_keyToSlot.TryGetValue(vk, out int slotIndex))
                {
                    if (down) FireSlot(slotIndex, true);
                    else if (up) FireSlot(slotIndex, false);
                }
            }
        }
        return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// Wraps _onSlotTriggered and tracks which slots are currently in a
    /// pressed-but-not-released state. The tracking is what lets the
    /// reconcile pass detect and correct stuck holds.
    private void FireSlot(int slotIndex, bool pressed)
    {
        bool shouldFire;
        lock (_sentPressLock)
        {
            shouldFire = pressed
                ? _sentPressSlots.Add(slotIndex)
                : _sentPressSlots.Remove(slotIndex);
        }

        if (!shouldFire)
            return;

        try
        {
            _onSlotTriggered(slotIndex, pressed);
        }
        catch (Exception ex)
        {
            Log.Write($"Input callback failed: slot={slotIndex + 1}, pressed={pressed}, error={ex.Message}");

            // If a release could not be delivered, keep the slot marked
            // pressed so the next reconcile pass can try to release it again.
            if (!pressed)
            {
                lock (_sentPressLock)
                    _sentPressSlots.Add(slotIndex);
            }
        }
    }

    private ushort _lastButtons;
    private byte _lastLT, _lastRT;
    private HashSet<string> _lastControllerNames = new(StringComparer.OrdinalIgnoreCase);

    private void PollControllers()
    {
        while (_running)
        {
            try
            {
                // Merge input across all four XInput user indexes. A DualSense
                // surfaced via Steam Input / ViGEm (or any second pad) can land
                // on a non-zero slot, so polling only index 0 silently misses
                // it. OR the buttons and take the max analog trigger across every
                // connected pad: a binding then fires no matter which index the
                // controller occupies.
                var current = ReadControllerState();

                if (current.AnyConnected)
                {
                    ushort buttons = current.Buttons;
                    byte lt = current.LT, rt = current.RT;
                    bool ltNow = lt > 128, rtNow = rt > 128;
                    bool ltWas = _lastLT > 128, rtWas = _lastRT > 128;

                    // Capture mode: report first newly pressed button
                    if (_capturing)
                    {
                        ushort newButtons = (ushort)(buttons & ~_lastButtons);
                        bool ltNew = ltNow && !ltWas;
                        bool rtNew = rtNow && !rtWas;
                        string? newName = FirstNewControllerName(current.ActiveNames, _lastControllerNames);

                        string? captured = null;
                        if (newName != null) captured = newName;
                        else if (ltNew) captured = "LT";
                        else if (rtNew) captured = "RT";
                        else if (newButtons != 0)
                        {
                            foreach (var kv in ButtonNameToFlag)
                            {
                                if ((newButtons & kv.Value) != 0)
                                {
                                    captured = kv.Key;
                                    break;
                                }
                            }
                        }

                        if (captured != null)
                        {
                            Log.Write($"Controller capture: {captured}");
                            _capturing = false;
                            _captureCallback?.Invoke(captured);
                            _captureCallback = null;
                        }
                    }
                    else
                    {
                        lock (_bindLock)
                        {
                            foreach (var cb in _controllerSlots)
                            {
                                bool nowActive = IsControllerBindingActive(cb, current.ActiveNames, buttons, ltNow, rtNow);
                                bool wasActive = IsControllerBindingActive(cb, _lastControllerNames, _lastButtons, ltWas, rtWas);

                                if (nowActive && !wasActive) FireSlot(cb.SlotIndex, true);
                                else if (!nowActive && wasActive) FireSlot(cb.SlotIndex, false);
                            }
                        }

                        // Reconcile pass: the primary event-driven path above
                        // can miss release events (focus transitions, LL-hook
                        // stalls, app-start-with-held-button, thread races).
                        // For every slot we think is held, verify the physical
                        // state via GetAsyncKeyState (keyboard) or live XInput
                        // state (controller). If nothing is physically held
                        // for this slot, synthesize a release to clear the
                        // stuck hold. Self-healing; cost is one pass per 16ms.
                        ReconcileHolds(current.ActiveNames, buttons, ltNow, rtNow);
                    }

                    _lastButtons = buttons;
                    _lastLT = lt;
                    _lastRT = rt;
                    _lastControllerNames = new HashSet<string>(current.ActiveNames, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // No controller connected (or error). Still run the
                    // reconcile pass so keyboard-only users also benefit
                    // from self-healing on missed key-up events. Pass zero
                    // controller state — reconcile will check keyboard
                    // GetAsyncKeyState as the authoritative physical state.
                    if (!_capturing) ReconcileHolds(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, false, false);
                    _lastButtons = 0;
                    _lastLT = 0;
                    _lastRT = 0;
                    _lastControllerNames.Clear();
                }
            }
            catch { }
            Thread.Sleep(16);
        }
    }

    private ControllerState ReadControllerState()
    {
        ushort buttons = 0;
        byte lt = 0, rt = 0;
        bool anyConnected = false;
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (uint userIndex = 0; userIndex < 4; userIndex++)
        {
            if (Native.XInputGetState(userIndex, out var state) != 0) continue;
            anyConnected = true;
            buttons |= state.Gamepad.wButtons;
            if (state.Gamepad.bLeftTrigger > lt) lt = state.Gamepad.bLeftTrigger;
            if (state.Gamepad.bRightTrigger > rt) rt = state.Gamepad.bRightTrigger;
        }

        AddXInputNames(buttons, lt, rt, activeNames);

        var directInput = ReadDirectInputState();
        if (directInput.AnyConnected)
        {
            anyConnected = true;
            buttons |= directInput.Buttons;
            if (directInput.LT > lt) lt = directInput.LT;
            if (directInput.RT > rt) rt = directInput.RT;
            activeNames.UnionWith(directInput.ActiveNames);
        }

        var rawInput = ReadRawInputState();
        if (rawInput.AnyConnected)
        {
            anyConnected = true;
            buttons |= rawInput.Buttons;
            if (rawInput.LT > lt) lt = rawInput.LT;
            if (rawInput.RT > rt) rt = rawInput.RT;
            activeNames.UnionWith(rawInput.ActiveNames);
        }

        AddXInputNames(buttons, lt, rt, activeNames);
        return new ControllerState(buttons, lt, rt, anyConnected, activeNames);
    }

    private ControllerState ReadRawInputState()
    {
        lock (_rawInputLock)
        {
            if ((DateTime.UtcNow - _lastRawInputAt).TotalMilliseconds > 750)
                return new ControllerState(0, 0, 0, false);

            return new ControllerState(_rawInputState.Buttons, _rawInputState.LT, _rawInputState.RT,
                _rawInputState.AnyConnected, new HashSet<string>(_rawInputState.ActiveNames, StringComparer.OrdinalIgnoreCase));
        }
    }

    private ControllerState ReadDirectInputState()
    {
        RefreshDirectInputPadsIfNeeded();

        ushort buttons = 0;
        byte lt = 0, rt = 0;
        bool anyConnected = false;
        bool shouldRefresh = false;
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pad in _directInputPads.ToArray())
        {
            try
            {
                pad.Device.Acquire();
                pad.Device.Poll();
                var state = pad.Device.GetCurrentJoystickState();
                anyConnected = true;

                var mapped = MapDirectInputState(pad, state);
                buttons |= mapped.Buttons;
                if (mapped.LT > lt) lt = mapped.LT;
                if (mapped.RT > rt) rt = mapped.RT;
                activeNames.UnionWith(mapped.ActiveNames);
            }
            catch
            {
                shouldRefresh = true;
            }
        }

        if (shouldRefresh)
            RefreshDirectInputPads(force: true);

        return new ControllerState(buttons, lt, rt, anyConnected, activeNames);
    }

    private void RefreshDirectInputPadsIfNeeded()
    {
        if ((DateTime.UtcNow - _lastDirectInputRefresh).TotalSeconds < 2)
            return;

        RefreshDirectInputPads(force: false);
    }

    private void RefreshDirectInputPads(bool force)
    {
        _lastDirectInputRefresh = DateTime.UtcNow;

        try
        {
            _directInput ??= DInput.DirectInput8Create();

            var instances = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                .Where(d => d.Type == DeviceType.Gamepad || d.Type == DeviceType.Joystick || d.IsHumanInterfaceDevice)
                .ToList();

            var known = new HashSet<Guid>(_directInputPads.Select(p => p.InstanceGuid));
            known.UnionWith(_directInputIgnoredPads);
            if (!force && instances.All(d => known.Contains(d.InstanceGuid)) && known.Count == instances.Count)
                return;

            DisposeDirectInputPads();
            _directInputIgnoredPads.Clear();

            foreach (var instance in instances)
            {
                try
                {
                    var device = _directInput.CreateDevice(instance.InstanceGuid);
                    device.SetDataFormat<RawJoystickState>();
                    try { device.SetCooperativeLevel(_ownerHandle, CooperativeLevel.Background | CooperativeLevel.NonExclusive); } catch { }
                    device.Acquire();

                    string name = $"{instance.ProductName} {instance.InstanceName}".Trim();
                    bool isSony = name.Contains("sony", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("dualsense", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("wireless controller", StringComparison.OrdinalIgnoreCase)
                        || instance.ProductGuid.ToString("N").Contains("054c", StringComparison.OrdinalIgnoreCase);

                    if (!isSony && IsXInputLikeDevice(name))
                    {
                        device.Dispose();
                        _directInputIgnoredPads.Add(instance.InstanceGuid);
                        Log.Write($"DirectInput controller skipped; using XInput instead: {name}");
                        continue;
                    }

                    _directInputPads.Add(new DirectInputPad
                    {
                        InstanceGuid = instance.InstanceGuid,
                        Name = name,
                        IsSony = isSony,
                        Device = device
                    });

                    Log.Write($"DirectInput controller attached: {name}");
                }
                catch (Exception ex)
                {
                    Log.Write($"DirectInput controller attach failed: {instance.ProductName}, error={ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"DirectInput refresh failed: {ex.Message}");
            DisposeDirectInputPads();
        }
    }

    private void DisposeDirectInputPads()
    {
        foreach (var pad in _directInputPads)
            pad.Dispose();
        _directInputPads.Clear();
    }

    private static ControllerState MapDirectInputState(DirectInputPad pad, JoystickState state)
    {
        ushort buttons = 0;
        byte lt = 0, rt = 0;
        bool[] diButtons = state.Buttons;
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Button(int index) => index >= 0 && index < diButtons.Length && diButtons[index];
        void Add(string name) => activeNames.Add(name);

        if (pad.IsSony)
        {
            if (Button(1)) { buttons |= Native.XINPUT_GAMEPAD_A; Add("A"); }              // Cross
            if (Button(2)) { buttons |= Native.XINPUT_GAMEPAD_B; Add("B"); }              // Circle
            if (Button(0)) { buttons |= Native.XINPUT_GAMEPAD_X; Add("X"); }              // Square
            if (Button(3)) { buttons |= Native.XINPUT_GAMEPAD_Y; Add("Y"); }              // Triangle
            if (Button(4)) { buttons |= Native.XINPUT_GAMEPAD_LEFT_SHOULDER; Add("LB"); }
            if (Button(5)) { buttons |= Native.XINPUT_GAMEPAD_RIGHT_SHOULDER; Add("RB"); }
            if (Button(8)) { buttons |= Native.XINPUT_GAMEPAD_BACK; Add("Back"); }        // Create
            if (Button(9)) { buttons |= Native.XINPUT_GAMEPAD_START; Add("Start"); }      // Options
            if (Button(10)) { buttons |= Native.XINPUT_GAMEPAD_LEFT_THUMB; Add("LS"); }
            if (Button(11)) { buttons |= Native.XINPUT_GAMEPAD_RIGHT_THUMB; Add("RS"); }
            if (Button(6)) { lt = 255; Add("LT"); }                                       // L2 digital edge
            if (Button(7)) { rt = 255; Add("RT"); }                                       // R2 digital edge
        }
        else
        {
            if (Button(0)) { buttons |= Native.XINPUT_GAMEPAD_A; Add("A"); }
            if (Button(1)) { buttons |= Native.XINPUT_GAMEPAD_B; Add("B"); }
            if (Button(2)) { buttons |= Native.XINPUT_GAMEPAD_X; Add("X"); }
            if (Button(3)) { buttons |= Native.XINPUT_GAMEPAD_Y; Add("Y"); }
            if (Button(4)) { buttons |= Native.XINPUT_GAMEPAD_LEFT_SHOULDER; Add("LB"); }
            if (Button(5)) { buttons |= Native.XINPUT_GAMEPAD_RIGHT_SHOULDER; Add("RB"); }
            if (Button(6)) { buttons |= Native.XINPUT_GAMEPAD_BACK; Add("Back"); }
            if (Button(7)) { buttons |= Native.XINPUT_GAMEPAD_START; Add("Start"); }
            if (Button(8)) { buttons |= Native.XINPUT_GAMEPAD_LEFT_THUMB; Add("LS"); }
            if (Button(9)) { buttons |= Native.XINPUT_GAMEPAD_RIGHT_THUMB; Add("RS"); }
        }

        for (int i = 0; i < diButtons.Length; i++)
        {
            if (diButtons[i])
                activeNames.Add($"DI{i + 1}");
        }

        ApplyPov(state.PointOfViewControllers, ref buttons, activeNames);
        return new ControllerState(buttons, lt, rt, true, activeNames);
    }

    private static bool IsXInputLikeDevice(string name)
    {
        return name.Contains("xbox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("xinput", StringComparison.OrdinalIgnoreCase)
            || name.Contains("360 for windows", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyPov(int[] povs, ref ushort buttons, HashSet<string> activeNames)
    {
        if (povs.Length == 0) return;

        int pov = povs[0];
        if (pov < 0 || pov >= 36000) return;

        if (pov >= 31500 || pov <= 4500) { buttons |= Native.XINPUT_GAMEPAD_DPAD_UP; activeNames.Add("DpadUp"); }
        if (pov >= 4500 && pov <= 13500) { buttons |= Native.XINPUT_GAMEPAD_DPAD_RIGHT; activeNames.Add("DpadRight"); }
        if (pov >= 13500 && pov <= 22500) { buttons |= Native.XINPUT_GAMEPAD_DPAD_DOWN; activeNames.Add("DpadDown"); }
        if (pov >= 22500 && pov <= 31500) { buttons |= Native.XINPUT_GAMEPAD_DPAD_LEFT; activeNames.Add("DpadLeft"); }
    }

    private static void AddXInputNames(ushort buttons, byte lt, byte rt, HashSet<string> activeNames)
    {
        foreach (var kv in ButtonNameToFlag)
        {
            if ((buttons & kv.Value) != 0)
                activeNames.Add(kv.Key);
        }

        if (lt > 128) activeNames.Add("LT");
        if (rt > 128) activeNames.Add("RT");
    }

    private static string? FirstNewControllerName(HashSet<string> current, HashSet<string> previous)
    {
        string[] preferred =
        {
            "LT", "RT", "A", "B", "X", "Y", "LB", "RB", "Start", "Back",
            "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "LS", "RS"
        };

        foreach (string name in preferred)
        {
            if (current.Contains(name) && !previous.Contains(name))
                return name;
        }

        return current
            .Where(name => !previous.Contains(name))
            .OrderBy(name => name.StartsWith("DI", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(2), out int n) ? n : int.MaxValue)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsControllerBindingActive(ControllerSlotBinding binding, HashSet<string> activeNames,
        ushort buttons, bool ltNow, bool rtNow)
    {
        if (binding.Names.Length != 0)
            return binding.Names.All(activeNames.Contains);

        bool active = (buttons & binding.ButtonMask) == binding.ButtonMask;
        if ((binding.TriggerMask & 1) != 0) active = active && ltNow;
        if ((binding.TriggerMask & 2) != 0) active = active && rtNow;
        return active;
    }

    private void RegisterRawInput()
    {
        try
        {
            var devices = new[]
            {
                new Native.RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x04, // Joystick
                    dwFlags = Native.RIDEV_INPUTSINK,
                    hwndTarget = _ownerHandle
                },
                new Native.RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x05, // Gamepad
                    dwFlags = Native.RIDEV_INPUTSINK,
                    hwndTarget = _ownerHandle
                }
            };

            bool ok = Native.RegisterRawInputDevices(devices, (uint)devices.Length,
                (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());

            Log.Write(ok
                ? "RawInput registered for game controllers"
                : $"RawInput registration failed: win32={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Write($"RawInput registration failed: {ex.Message}");
        }
    }

    private static bool TryParsePlayStationReport(byte[] report, out ControllerState state)
    {
        state = new ControllerState(0, 0, 0, false);
        if (report.Length < 11) return false;

        int buttonsOffset = report[0] switch
        {
            0x01 => 8, // USB/simple HID
            0x31 => 9, // Bluetooth extended HID
            _ => -1
        };

        if (buttonsOffset < 0 || buttonsOffset + 2 >= report.Length)
            return false;

        byte faceAndDpad = report[buttonsOffset];
        byte shoulders = report[buttonsOffset + 1];
        byte system = report[buttonsOffset + 2];
        byte dpad = (byte)(faceAndDpad & 0x0F);
        ushort buttons = 0;
        byte lt = 0, rt = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, ushort flag)
        {
            names.Add(name);
            buttons |= flag;
        }

        if ((faceAndDpad & 0x10) != 0) Add("X", Native.XINPUT_GAMEPAD_X); // Square
        if ((faceAndDpad & 0x20) != 0) Add("A", Native.XINPUT_GAMEPAD_A); // Cross
        if ((faceAndDpad & 0x40) != 0) Add("B", Native.XINPUT_GAMEPAD_B); // Circle
        if ((faceAndDpad & 0x80) != 0) Add("Y", Native.XINPUT_GAMEPAD_Y); // Triangle

        if ((shoulders & 0x01) != 0) Add("LB", Native.XINPUT_GAMEPAD_LEFT_SHOULDER);
        if ((shoulders & 0x02) != 0) Add("RB", Native.XINPUT_GAMEPAD_RIGHT_SHOULDER);
        if ((shoulders & 0x04) != 0) { names.Add("LT"); lt = 255; }
        if ((shoulders & 0x08) != 0) { names.Add("RT"); rt = 255; }
        if ((shoulders & 0x10) != 0) Add("Back", Native.XINPUT_GAMEPAD_BACK); // Create/Share
        if ((shoulders & 0x20) != 0) Add("Start", Native.XINPUT_GAMEPAD_START); // Options
        if ((shoulders & 0x40) != 0) Add("LS", Native.XINPUT_GAMEPAD_LEFT_THUMB);
        if ((shoulders & 0x80) != 0) Add("RS", Native.XINPUT_GAMEPAD_RIGHT_THUMB);

        if ((system & 0x01) != 0) names.Add("PS");
        if ((system & 0x02) != 0) names.Add("Touchpad");
        if ((system & 0x04) != 0) names.Add("Mic");

        AddDpadFromNibble(dpad, names, ref buttons);

        if (TryReadTriggerAxis(report, buttonsOffset, left: true, out byte analogLt) && analogLt > lt)
        {
            lt = analogLt;
            if (lt > 128) names.Add("LT");
        }

        if (TryReadTriggerAxis(report, buttonsOffset, left: false, out byte analogRt) && analogRt > rt)
        {
            rt = analogRt;
            if (rt > 128) names.Add("RT");
        }

        state = new ControllerState(buttons, lt, rt, true, names);
        return true;
    }

    private static bool TryReadTriggerAxis(byte[] report, int buttonsOffset, bool left, out byte value)
    {
        value = 0;
        int axisOffset = buttonsOffset - (left ? 3 : 2);
        if (axisOffset < 0 || axisOffset >= report.Length)
            return false;

        value = report[axisOffset];
        return true;
    }

    private static void AddDpadFromNibble(byte dpad, HashSet<string> names, ref ushort buttons)
    {
        if (dpad > 7) return;

        if (dpad == 7 || dpad == 0 || dpad == 1)
        {
            names.Add("DpadUp");
            buttons |= Native.XINPUT_GAMEPAD_DPAD_UP;
        }

        if (dpad == 1 || dpad == 2 || dpad == 3)
        {
            names.Add("DpadRight");
            buttons |= Native.XINPUT_GAMEPAD_DPAD_RIGHT;
        }

        if (dpad == 3 || dpad == 4 || dpad == 5)
        {
            names.Add("DpadDown");
            buttons |= Native.XINPUT_GAMEPAD_DPAD_DOWN;
        }

        if (dpad == 5 || dpad == 6 || dpad == 7)
        {
            names.Add("DpadLeft");
            buttons |= Native.XINPUT_GAMEPAD_DPAD_LEFT;
        }
    }

    private void LogRawInputChange(HashSet<string> names)
    {
        if (!_capturing)
            return;

        if (names.SetEquals(_lastLoggedRawNames))
            return;

        _lastLoggedRawNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return;

        Log.Write($"RawInput controller state: {string.Join("+", names.OrderBy(n => n))}");
    }

    /// For each slot currently marked pressed-but-not-released, check whether
    /// any of its physical bindings (keyboard or controller) is actually held
    /// right now. If none are, fire a synthetic release to clear the stuck
    /// hold. Invariant guaranteed by this pass: a slot is held in
    /// _sentPressSlots only while at least one physical binding for it is
    /// actually held.
    private void ReconcileHolds(HashSet<string> activeNames, ushort buttons, bool ltNow, bool rtNow)
    {
        // Snapshot the current set of tracked presses so we don't hold the
        // lock during physical-state reads (GetAsyncKeyState is a user32
        // syscall; XInput we already have in-hand via the arguments).
        int[] toCheck;
        lock (_sentPressLock)
        {
            if (_sentPressSlots.Count == 0) return;
            toCheck = new int[_sentPressSlots.Count];
            _sentPressSlots.CopyTo(toCheck);
        }

        foreach (int slotIdx in toCheck)
        {
            bool physicallyHeld = false;

            lock (_bindLock)
            {
                // Keyboard binding check: any key bound to this slot pressed?
                foreach (var kv in _keyToSlot)
                {
                    if (kv.Value != slotIdx) continue;
                    if ((Native.GetAsyncKeyState(kv.Key) & 0x8000) != 0)
                    {
                        physicallyHeld = true;
                        break;
                    }
                }

                // Controller binding check: any combo bound to this slot active?
                if (!physicallyHeld)
                {
                    foreach (var cb in _controllerSlots)
                    {
                        if (cb.SlotIndex != slotIdx) continue;
                        bool active = IsControllerBindingActive(cb, activeNames, buttons, ltNow, rtNow);
                        if (active)
                        {
                            physicallyHeld = true;
                            break;
                        }
                    }
                }
            }

            if (!physicallyHeld)
            {
                // Synthetic release: clears the stuck hold and updates our
                // tracking. FireSlot itself removes slotIdx from _sentPressSlots.
                Log.Write($"Synthetic hold release: slot={slotIdx + 1}");
                FireSlot(slotIdx, false);
            }
        }
    }

    private void ReleaseTrackedSlots(string reason)
    {
        int[] toRelease;
        lock (_sentPressLock)
        {
            if (_sentPressSlots.Count == 0) return;
            toRelease = new int[_sentPressSlots.Count];
            _sentPressSlots.CopyTo(toRelease);
            _sentPressSlots.Clear();
        }

        foreach (int slotIdx in toRelease)
        {
            try
            {
                Log.Write($"Forced hold release: slot={slotIdx + 1}, reason={reason}");
                _onSlotTriggered(slotIdx, false);
            }
            catch (Exception ex)
            {
                Log.Write($"Forced release callback failed: slot={slotIdx + 1}, error={ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
