using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Fast;

class MainForm : Form
{
    private const string AppVersion = "1.0.5";
    private readonly AppSettings _settings;
    private readonly SpeedManager _speedMgr;
    private readonly ProcessWatcher _watcher;
    private readonly InputManager _inputMgr;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly int _uiThreadId;

    // Slot UI rows
    private readonly SlotRow[] _slotRows = new SlotRow[10];

    // Process list
    private readonly ListBox _processList;
    private readonly Button _pickProcessBtn;
    private readonly Button _addProcessBtn;
    private readonly Button _removeProcessBtn;
    private readonly Label _statusLabel;

    // Settings / footer
    private readonly CheckBox _startupCheck;

    // Tray
    private readonly NotifyIcon _trayIcon;

    private string _dllPath;
    private int _capturingSlot = -1; // which slot is waiting for a key

    struct SlotRow
    {
        public Label IndexLabel;
        public Button HotkeyBtn;
        public TextBox SpeedBox;
        public CheckBox HoldCheck;
        public Button ClearBtn;
    }

    public MainForm()
    {
        _uiThreadId = Thread.CurrentThread.ManagedThreadId;
        Log.Write("Fast starting");
        // Load settings
        _settings = AppSettings.Load();

        // Resolve DLL path
        _dllPath = _settings.HookDllPath;
        if (!Path.IsPathRooted(_dllPath))
            _dllPath = Path.Combine(AppContext.BaseDirectory, _dllPath);

        // Core components
        _speedMgr = new SpeedManager(_settings);
        _watcher = new ProcessWatcher(_settings, _dllPath);
        _inputMgr = new InputManager(OnSlotTriggered, Handle);

        // --- Form setup ---
        Text = AppTitle();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(520, 520);
        BackColor = Color.FromArgb(46, 46, 50);
        ForeColor = Color.FromArgb(240, 240, 240);

        // --- Speed Slots panel ---
        var slotsGroup = new GroupBox
        {
            Text = "Speed Slots",
            ForeColor = Color.FromArgb(210, 210, 215),
            Location = new Point(10, 8),
            Size = new Size(496, 300)
        };
        Controls.Add(slotsGroup);

        // Header
        var hdrHotkey = new Label { Text = "Hotkey", Location = new Point(40, 18), Size = new Size(140, 16), ForeColor = Color.FromArgb(160, 160, 170), Font = new Font("Segoe UI", 7.5f) };
        var hdrSpeed = new Label { Text = "Speed", Location = new Point(190, 18), Size = new Size(70, 16), ForeColor = Color.FromArgb(160, 160, 170), Font = new Font("Segoe UI", 7.5f) };
        var hdrHold = new Label { Text = "Hold", Location = new Point(275, 18), Size = new Size(40, 16), ForeColor = Color.FromArgb(160, 160, 170), Font = new Font("Segoe UI", 7.5f) };
        slotsGroup.Controls.AddRange(new Control[] { hdrHotkey, hdrSpeed, hdrHold });

        for (int i = 0; i < 10; i++)
        {
            int y = 36 + i * 26;
            int idx = i;

            var indexLbl = new Label
            {
                Text = $"{i + 1}.",
                Location = new Point(8, y + 2),
                Size = new Size(26, 20),
                ForeColor = Color.FromArgb(155, 155, 160),
                TextAlign = ContentAlignment.MiddleRight
            };

            var hotkeyBtn = new Button
            {
                Text = _settings.Slots[i].BindingDisplay,
                Location = new Point(40, y),
                Size = new Size(140, 23),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(62, 62, 66),
                ForeColor = Color.FromArgb(230, 230, 235),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            hotkeyBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
            hotkeyBtn.Click += (_, _) => StartCaptureForSlot(idx);

            var speedBox = new TextBox
            {
                Text = _settings.Slots[i].HasSpeed ? _settings.Slots[i].Speed.ToString("G") : "",
                Location = new Point(190, y),
                Size = new Size(70, 23),
                BackColor = Color.FromArgb(58, 58, 62),
                ForeColor = Color.FromArgb(240, 240, 240),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center
            };
            speedBox.Leave += (_, _) => OnSpeedEdited(idx);
            speedBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OnSpeedEdited(idx); e.SuppressKeyPress = true; } };

            var holdChk = new CheckBox
            {
                Checked = _settings.Slots[i].Hold,
                Location = new Point(280, y + 2),
                Size = new Size(16, 16),
                FlatStyle = FlatStyle.Flat
            };
            holdChk.CheckedChanged += (_, _) => OnHoldChanged(idx);

            var clearBtn = new Button
            {
                Text = "X",
                Location = new Point(310, y),
                Size = new Size(26, 23),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(62, 62, 66),
                ForeColor = Color.FromArgb(200, 100, 100),
                Cursor = Cursors.Hand
            };
            clearBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
            clearBtn.Click += (_, _) => ClearSlot(idx);

            slotsGroup.Controls.AddRange(new Control[] { indexLbl, hotkeyBtn, speedBox, holdChk, clearBtn });
            _slotRows[i] = new SlotRow
            {
                IndexLabel = indexLbl,
                HotkeyBtn = hotkeyBtn,
                SpeedBox = speedBox,
                HoldCheck = holdChk,
                ClearBtn = clearBtn
            };
        }

        // --- Process list ---
        var procGroup = new GroupBox
        {
            Text = "Watched Processes",
            ForeColor = Color.FromArgb(210, 210, 215),
            Location = new Point(10, 314),
            Size = new Size(496, 160)
        };
        Controls.Add(procGroup);

        _processList = new ListBox
        {
            Location = new Point(10, 20),
            Size = new Size(370, 128),
            BackColor = Color.FromArgb(54, 54, 58),
            ForeColor = Color.FromArgb(235, 235, 235),
            BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = SelectionMode.One
        };
        procGroup.Controls.Add(_processList);
        RefreshProcessList();

        _pickProcessBtn = new Button
        {
            Text = "Pick...",
            Location = new Point(390, 20),
            Size = new Size(94, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(62, 62, 66),
            ForeColor = Color.FromArgb(235, 235, 235),
            Cursor = Cursors.Hand
        };
        _pickProcessBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
        _pickProcessBtn.Click += OnPickProcess;
        procGroup.Controls.Add(_pickProcessBtn);

        _addProcessBtn = new Button
        {
            Text = "+ Manual",
            Location = new Point(390, 54),
            Size = new Size(94, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(62, 62, 66),
            ForeColor = Color.FromArgb(200, 200, 205),
            Cursor = Cursors.Hand
        };
        _addProcessBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
        _addProcessBtn.Click += OnAddProcess;
        procGroup.Controls.Add(_addProcessBtn);

        _removeProcessBtn = new Button
        {
            Text = "- Remove",
            Location = new Point(390, 88),
            Size = new Size(94, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(62, 62, 66),
            ForeColor = Color.FromArgb(235, 235, 235),
            Cursor = Cursors.Hand
        };
        _removeProcessBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
        _removeProcessBtn.Click += OnRemoveProcess;
        procGroup.Controls.Add(_removeProcessBtn);

        // --- Launch on startup ---
        _startupCheck = new CheckBox
        {
            Text = "Launch on startup",
            Location = new Point(360, 484),
            Size = new Size(146, 24),
            Checked = StartupManager.IsEnabled(),
            ForeColor = Color.FromArgb(210, 210, 215),
            FlatStyle = FlatStyle.Flat
        };
        _startupCheck.CheckedChanged += StartupCheckChanged;
        Controls.Add(_startupCheck);

        // --- Status bar ---
        _statusLabel = new Label
        {
            Text = "Speed: 1.0x (normal)",
            Location = new Point(10, 480),
            Size = new Size(400, 30),
            ForeColor = Color.FromArgb(185, 185, 190),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Consolas", 11f, FontStyle.Bold)
        };
        Controls.Add(_statusLabel);

        // --- Tray icon ---
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show Fast", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => { Application.Exit(); });

        _trayIcon = new NotifyIcon
        {
            Text = "Fast - Game Speed Controller",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        // --- Wire events ---
        _watcher.OnAttach += ap =>
        {
            // Auto-remember this process
            _settings.AddProcess(ap.Name + ".exe");
            RefreshWatcherUi();
        };
        _watcher.OnDetach += pid => RefreshWatcherUi();

        _speedMgr.SpeedChanged += (speed, active) =>
        {
            Log.Write($"SpeedChanged event: speed={speed:F2}, active={active}");
            _watcher.UpdateAllSpeeds(speed, active);
            PostUi(() => UpdateSpeedDisplay(speed, active));
        };

        // --- Scan timer ---
        _scanTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _scanTimer.Tick += (_, _) =>
        {
            _watcher.Scan();
            RefreshProcessList();
        };
        _scanTimer.Start();

        // Start input
        _inputMgr.RebuildBindings(_settings.Slots);
        _inputMgr.Start();

        // Initial scan
        _watcher.Scan();
        RefreshProcessList();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT)
            _inputMgr?.ProcessRawInput(m.LParam);

        base.WndProc(ref m);
    }

    private void StartupCheckChanged(object? sender, EventArgs e)
    {
        try
        {
            StartupManager.SetEnabled(_startupCheck.Checked);
        }
        catch (Exception ex)
        {
            _startupCheck.CheckedChanged -= StartupCheckChanged;
            _startupCheck.Checked = StartupManager.IsEnabled();
            _startupCheck.CheckedChanged += StartupCheckChanged;
            MessageBox.Show($"Failed to update startup setting:\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnSlotTriggered(int slotIndex, bool pressed)
    {
        _speedMgr.HandleSlot(slotIndex, pressed);
    }

    private void RefreshWatcherUi()
    {
        PostUi(() =>
        {
            RefreshProcessList();
            UpdateStatus();
        });
    }

    private void PostUi(Action action)
    {
        if (IsDisposed) return;

        if (!IsHandleCreated)
        {
            if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
                action();
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            if (!IsDisposed)
                action();
        }
    }

    private void StartCaptureForSlot(int slot)
    {
        _capturingSlot = slot;
        _slotRows[slot].HotkeyBtn.Text = "< press a key >";
        _slotRows[slot].HotkeyBtn.BackColor = Color.FromArgb(95, 75, 30);

        _inputMgr.StartCapture(keyName =>
        {
            PostUi(() => FinishCapture(slot, keyName));
        });
    }

    private void FinishCapture(int slot, string keyName)
    {
        if (slot < 0 || slot >= 10) return;

        // Determine if this is a keyboard key or controller button
        bool isController = InputManager.ButtonNameToFlag.ContainsKey(keyName)
                         || keyName == "LT" || keyName == "RT";

        var s = _settings.Slots[slot];
        if (isController)
        {
            s.Controller = keyName;
            // Keep existing keyboard binding if any
        }
        else
        {
            s.Key = keyName;
            // Keep existing controller binding if any
        }

        _slotRows[slot].HotkeyBtn.Text = s.BindingDisplay;
        _slotRows[slot].HotkeyBtn.BackColor = Color.FromArgb(62, 62, 66);
        _capturingSlot = -1;

        _settings.Save();
        _inputMgr.RebuildBindings(_settings.Slots);
    }

    private void OnSpeedEdited(int slot)
    {
        var box = _slotRows[slot].SpeedBox;
        if (double.TryParse(box.Text, out double val) && val > 0)
        {
            _settings.Slots[slot].Speed = val;
            _settings.Save();
            _inputMgr.RebuildBindings(_settings.Slots);
        }
        else if (string.IsNullOrWhiteSpace(box.Text))
        {
            _settings.Slots[slot].Speed = 1.0;
            _settings.Save();
            _inputMgr.RebuildBindings(_settings.Slots);
        }
    }

    private void OnHoldChanged(int slot)
    {
        _settings.Slots[slot].Hold = _slotRows[slot].HoldCheck.Checked;
        _settings.Save();
    }

    private void ClearSlot(int slot)
    {
        _settings.Slots[slot].Key = null;
        _settings.Slots[slot].Controller = null;
        _settings.Slots[slot].Speed = 1.0;
        _settings.Slots[slot].Hold = false;

        _slotRows[slot].HotkeyBtn.Text = "";
        _slotRows[slot].SpeedBox.Text = "";
        _slotRows[slot].HoldCheck.Checked = false;
        _slotRows[slot].HotkeyBtn.BackColor = Color.FromArgb(62, 62, 66);

        _settings.Save();
        _inputMgr.RebuildBindings(_settings.Slots);
        _speedMgr.Reset();
    }

    private void OnPickProcess(object? sender, EventArgs e)
    {
        using var dlg = new ProcessPickerDialog(_settings.Processes);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            foreach (var selected in dlg.SelectedProcesses)
            {
                _settings.AddProcess(selected.ProcessName);
                _watcher.PreferProcess(selected.ProcessName, selected.Pid);
            }
            RefreshProcessList();
            _watcher.Scan();
        }
    }

    private void OnAddProcess(object? sender, EventArgs e)
    {
        using var dlg = new InputDialog("Add Process", "Process name (e.g. game.exe):");
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            _settings.AddProcess(dlg.Value);
            RefreshProcessList();
        }
    }

    private void OnRemoveProcess(object? sender, EventArgs e)
    {
        if (_processList.SelectedItem is WatchedProcessListItem item)
        {
            _settings.RemoveProcess(item.ProcessName);
            RefreshProcessList();
        }
    }

    private void RefreshProcessList()
    {
        string? selectedProcess = (_processList.SelectedItem as WatchedProcessListItem)?.ProcessName;
        string? topProcess = null;
        if (_processList.Items.Count > 0 && _processList.TopIndex >= 0 && _processList.TopIndex < _processList.Items.Count)
            topProcess = (_processList.Items[_processList.TopIndex] as WatchedProcessListItem)?.ProcessName;

        var items = _settings.Processes
            .Select(proc => new { ProcessName = proc, Status = _watcher.GetStatus(proc) })
            .OrderByDescending(item => IsAttached(item.Status))
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new WatchedProcessListItem(item.ProcessName,
                $"{item.ProcessName}  {DescribeProcessStatus(item.Status)}"))
            .ToList();

        _processList.BeginUpdate();
        try
        {
            _processList.Items.Clear();
            foreach (var item in items)
                _processList.Items.Add(item);

            RestoreProcessListSelection(selectedProcess);
            RestoreProcessListTop(topProcess);
        }
        finally
        {
            _processList.EndUpdate();
        }
    }

    private void RestoreProcessListSelection(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
            return;

        for (int i = 0; i < _processList.Items.Count; i++)
        {
            if (_processList.Items[i] is WatchedProcessListItem item &&
                item.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                _processList.SelectedIndex = i;
                return;
            }
        }
    }

    private void RestoreProcessListTop(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
            return;

        for (int i = 0; i < _processList.Items.Count; i++)
        {
            if (_processList.Items[i] is WatchedProcessListItem item &&
                item.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                _processList.TopIndex = Math.Min(i, Math.Max(0, _processList.Items.Count - 1));
                return;
            }
        }
    }

    private static bool IsAttached(ProcessScanStatus? status) => status?.AttachedPid.HasValue == true;

    private static string DescribeProcessStatus(ProcessScanStatus? status)
    {
        if (status == null)
            return "[not scanned]";

        if (status.AttachedPid.HasValue)
        {
            string count = status.MatchCount > 1 ? $"; {status.MatchCount} found" : "";
            return $"[ATTACHED pid {status.AttachedPid.Value}{count}]";
        }

        if (status.MatchCount <= 0)
            return "[not running]";

        string candidate = status.CandidatePid.HasValue ? $"pid {status.CandidatePid.Value}" : $"{status.MatchCount} found";
        if (!string.IsNullOrWhiteSpace(status.LastError))
            return $"[FOUND {candidate}, ATTACH FAILED: {ShortenStatus(status.LastError)}]";

        return status.MatchCount > 1
            ? $"[{status.MatchCount} found, using {candidate}]"
            : $"[FOUND {candidate}]";
    }

    private static string ShortenStatus(string value)
    {
        const int max = 86;
        value = value.Replace(Environment.NewLine, " ").Trim();
        return value.Length <= max ? value : value[..(max - 3)] + "...";
    }

    private void UpdateSpeedDisplay(double speed, bool active)
    {
        if (active)
        {
            _statusLabel.Text = $"Speed: {speed:F2}x";
            _statusLabel.ForeColor = speed > 1.0
                ? Color.FromArgb(100, 220, 255)
                : Color.FromArgb(255, 200, 100);
        }
        else
        {
            _statusLabel.Text = "Speed: 1.0x (normal)";
            _statusLabel.ForeColor = Color.FromArgb(185, 185, 190);
        }
    }

    private void UpdateStatus()
    {
        int count = _watcher.Attached.Count;
        Text = AppTitle(count);
        RefreshProcessList();
    }

    private static string AppTitle(int attachedCount = 0)
    {
        return attachedCount > 0 ? $"Fast v{AppVersion}  [{attachedCount} attached]" : $"Fast v{AppVersion}";
    }

    private void RestoreFromTray()
    {
        _trayIcon.Visible = false;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            _trayIcon.Visible = true;
            Hide();
            _trayIcon.ShowBalloonTip(1500, "Fast", "Still running. Double-click tray icon to restore.", ToolTipIcon.Info);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Log.Write("Fast closing");
        _scanTimer.Stop();
        _inputMgr.Stop();
        _watcher.DetachAll();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnFormClosing(e);
    }
}

sealed class WatchedProcessListItem
{
    public string ProcessName { get; }
    private readonly string _display;

    public WatchedProcessListItem(string processName, string display)
    {
        ProcessName = processName;
        _display = display;
    }

    public override string ToString() => _display;
}

// Dialog that lists running processes for selection
class ProcessPickerDialog : Form
{
    private readonly CheckedListBox _listBox;
    private readonly TextBox _filterBox;
    private readonly List<ProcessPickerItem> _allEntries = new();
    private readonly HashSet<string> _alreadyWatched;

    public List<ProcessPickerSelection> SelectedProcesses { get; } = new();

    public ProcessPickerDialog(IEnumerable<string> alreadyWatched)
    {
        _alreadyWatched = new HashSet<string>(alreadyWatched, StringComparer.OrdinalIgnoreCase);

        Text = "Pick Process";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 440);
        BackColor = Color.FromArgb(46, 46, 50);
        ForeColor = Color.FromArgb(240, 240, 240);

        var filterLabel = new Label
        {
            Text = "Filter:",
            Location = new Point(12, 12),
            Size = new Size(40, 20)
        };
        Controls.Add(filterLabel);

        _filterBox = new TextBox
        {
            Location = new Point(56, 10),
            Size = new Size(350, 24),
            BackColor = Color.FromArgb(58, 58, 62),
            ForeColor = Color.FromArgb(240, 240, 240),
            BorderStyle = BorderStyle.FixedSingle
        };
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        Controls.Add(_filterBox);

        _listBox = new CheckedListBox
        {
            Location = new Point(12, 40),
            Size = new Size(394, 348),
            BackColor = Color.FromArgb(54, 54, 58),
            ForeColor = Color.FromArgb(235, 235, 235),
            BorderStyle = BorderStyle.FixedSingle,
            CheckOnClick = true
        };
        Controls.Add(_listBox);

        var okBtn = new Button
        {
            Text = "Add Selected",
            DialogResult = DialogResult.OK,
            Location = new Point(230, 396),
            Size = new Size(100, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(72, 72, 76),
            ForeColor = Color.FromArgb(235, 235, 235)
        };
        okBtn.Click += (_, _) => CollectSelected();
        var cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(336, 396),
            Size = new Size(70, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(72, 72, 76),
            ForeColor = Color.FromArgb(235, 235, 235)
        };
        Controls.AddRange(new Control[] { okBtn, cancelBtn });
        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        PopulateProcesses();
    }

    private void PopulateProcesses()
    {
        var entries = new List<ProcessPickerItem>();

        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                string pName = proc.ProcessName;
                string exeName = pName + ".exe";

                // Skip processes with no main window title AND very low memory (likely services)
                // But keep anything with a window, or with >20MB working set
                bool hasWindow = false;
                long memMb = 0;
                try
                {
                    hasWindow = !string.IsNullOrEmpty(proc.MainWindowTitle);
                    memMb = proc.WorkingSet64 / (1024 * 1024);
                }
                catch { }

                if (!hasWindow && memMb < 20)
                    continue;

                string watched = _alreadyWatched.Contains(exeName) ? "  [watched]" : "";
                string display = hasWindow
                    ? $"{exeName}  [pid {proc.Id}]  -  {proc.MainWindowTitle}{watched}"
                    : $"{exeName}  [pid {proc.Id}]  ({memMb} MB){watched}";

                entries.Add(new ProcessPickerItem(display, exeName, proc.Id, hasWindow));
            }
            catch { }
        }

        // Sort: windowed processes first, then by name
        entries.Sort((a, b) =>
        {
            if (a.HasWindow != b.HasWindow) return b.HasWindow.CompareTo(a.HasWindow);
            int name = string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : a.Pid.CompareTo(b.Pid);
        });

        _allEntries.Clear();
        _listBox.Items.Clear();
        foreach (var entry in entries)
        {
            _allEntries.Add(entry);
            _listBox.Items.Add(entry);
        }
    }

    private void ApplyFilter()
    {
        string filter = _filterBox.Text.Trim();
        _listBox.Items.Clear();
        foreach (var entry in _allEntries)
        {
            if (string.IsNullOrEmpty(filter) ||
                entry.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _listBox.Items.Add(entry);
        }
    }

    private void CollectSelected()
    {
        SelectedProcesses.Clear();
        foreach (var item in _listBox.CheckedItems)
        {
            if (item is ProcessPickerItem process)
                SelectedProcesses.Add(new ProcessPickerSelection(process.ProcessName, process.Pid));
        }
    }
}

sealed class ProcessPickerSelection
{
    public string ProcessName { get; }
    public int Pid { get; }

    public ProcessPickerSelection(string processName, int pid)
    {
        ProcessName = processName;
        Pid = pid;
    }
}

sealed class ProcessPickerItem
{
    public string Display { get; }
    public string ProcessName { get; }
    public int Pid { get; }
    public bool HasWindow { get; }

    public ProcessPickerItem(string display, string processName, int pid, bool hasWindow)
    {
        Display = display;
        ProcessName = processName;
        Pid = pid;
        HasWindow = hasWindow;
    }

    public override string ToString() => Display;
}

// Settings dialog with Launch on Startup toggle
class SettingsDialog : Form
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Fast";

    public SettingsDialog()
    {
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 100);
        BackColor = Color.FromArgb(46, 46, 50);
        ForeColor = Color.FromArgb(240, 240, 240);

        var startupCheck = new CheckBox
        {
            Text = "Launch on Windows startup",
            Location = new Point(20, 20),
            Size = new Size(270, 24),
            Checked = IsStartupEnabled(),
            ForeColor = Color.FromArgb(235, 235, 235),
            FlatStyle = FlatStyle.Flat
        };
        startupCheck.CheckedChanged += (_, _) => SetStartup(startupCheck.Checked);
        Controls.Add(startupCheck);

        var closeBtn = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Location = new Point(220, 60),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(72, 72, 76),
            ForeColor = Color.FromArgb(235, 235, 235)
        };
        Controls.Add(closeBtn);
        AcceptButton = closeBtn;
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to update startup setting:\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

// Simple input dialog for adding process names
class InputDialog : Form
{
    public string Value => _textBox.Text;
    private readonly TextBox _textBox;

    public InputDialog(string title, string prompt)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 110);
        BackColor = Color.FromArgb(46, 46, 50);
        ForeColor = Color.FromArgb(240, 240, 240);

        var lbl = new Label { Text = prompt, Location = new Point(12, 12), Size = new Size(296, 20) };
        Controls.Add(lbl);

        _textBox = new TextBox
        {
            Location = new Point(12, 36),
            Size = new Size(296, 24),
            BackColor = Color.FromArgb(58, 58, 62),
            ForeColor = Color.FromArgb(240, 240, 240),
            BorderStyle = BorderStyle.FixedSingle
        };
        _textBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); } };
        Controls.Add(_textBox);

        var okBtn = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(152, 70),
            Size = new Size(75, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(72, 72, 76),
            ForeColor = Color.FromArgb(235, 235, 235)
        };
        var cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(233, 70),
            Size = new Size(75, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(72, 72, 76),
            ForeColor = Color.FromArgb(235, 235, 235)
        };
        Controls.AddRange(new Control[] { okBtn, cancelBtn });
        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }
}
