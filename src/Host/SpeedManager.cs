using System;
using System.Collections.Generic;

namespace Fast;

class SpeedManager
{
    private readonly AppSettings _settings;
    private readonly object _lock = new();
    private readonly List<int> _heldSlots = new();
    private int _toggledSlot = -1;
    private double _baseSpeed = 1.0;

    public double CurrentSpeed
    {
        get
        {
            lock (_lock) return CurrentSpeedLocked();
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_lock) return IsActiveLocked();
        }
    }

    public event Action<double, bool>? SpeedChanged;

    public SpeedManager(AppSettings settings)
    {
        _settings = settings;
    }

    public void HandleSlot(int slotIndex, bool pressed)
    {
        double speed;
        bool active;

        lock (_lock)
        {
            if (slotIndex < 0 || slotIndex >= _settings.Slots.Count) return;
            var slot = _settings.Slots[slotIndex];
            if (!slot.HasSpeed) return;

            if (slot.Hold)
            {
                if (pressed)
                {
                    if (!_heldSlots.Contains(slotIndex))
                        _heldSlots.Add(slotIndex);
                }
                else
                {
                    _heldSlots.RemoveAll(slot => slot == slotIndex);
                }
            }
            else // Toggle
            {
                if (!pressed) return; // only act on key-down for toggle

                if (_toggledSlot == slotIndex)
                {
                    _toggledSlot = -1;
                    _baseSpeed = 1.0;
                }
                else
                {
                    _toggledSlot = slotIndex;
                    _baseSpeed = slot.Speed;
                }
            }

            speed = CurrentSpeedLocked();
            active = IsActiveLocked(speed);
        }

        SpeedChanged?.Invoke(speed, active);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _heldSlots.Clear();
            _toggledSlot = -1;
            _baseSpeed = 1.0;
        }
        SpeedChanged?.Invoke(1.0, false);
    }

    private double CurrentSpeedLocked()
    {
        for (int i = _heldSlots.Count - 1; i >= 0; i--)
        {
            int slotIndex = _heldSlots[i];
            if (slotIndex < 0 || slotIndex >= _settings.Slots.Count)
            {
                _heldSlots.RemoveAt(i);
                continue;
            }

            var slot = _settings.Slots[slotIndex];
            if (slot.Hold && slot.HasSpeed)
                return slot.Speed;

            _heldSlots.RemoveAt(i);
        }

        return _baseSpeed;
    }

    private bool IsActiveLocked() => IsActiveLocked(CurrentSpeedLocked());

    private static bool IsActiveLocked(double speed) => Math.Abs(speed - 1.0) > 0.001;
}
