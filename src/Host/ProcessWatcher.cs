using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;

namespace Fast;

class AttachedProcess
{
    public int Pid { get; init; }
    public string TargetName { get; init; } = "";
    public string Name { get; init; } = "";
    public string WindowTitle { get; init; } = "";
    public SharedMemory SharedMem { get; init; } = null!;
}

class ProcessScanStatus
{
    public string TargetName { get; init; } = "";
    public int MatchCount { get; set; }
    public int? CandidatePid { get; set; }
    public string CandidateName { get; set; } = "";
    public string CandidateWindowTitle { get; set; } = "";
    public int? AttachedPid { get; set; }
    public string AttachedName { get; set; } = "";
    public string AttachedWindowTitle { get; set; } = "";
    public string? LastError { get; set; }
}

class ProcessWatcher
{
    private readonly AppSettings _settings;
    private readonly string _dllPath;
    private readonly object _attachedLock = new();
    private readonly Dictionary<int, AttachedProcess> _attached = new();
    private readonly Dictionary<string, ProcessScanStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _preferredPids = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<int, AttachedProcess> Attached
    {
        get
        {
            lock (_attachedLock)
                return new Dictionary<int, AttachedProcess>(_attached);
        }
    }
    public IReadOnlyDictionary<string, ProcessScanStatus> Statuses => _statuses;
    public event Action<AttachedProcess>? OnAttach;
    public event Action<int>? OnDetach;

    public ProcessWatcher(AppSettings settings, string dllPath)
    {
        _settings = settings;
        _dllPath = dllPath;
    }

    public void PreferProcess(string processName, int pid)
    {
        string target = NormalizeProcessName(processName);
        if (!string.IsNullOrEmpty(target))
            _preferredPids[target] = pid;
    }

    public ProcessScanStatus? GetStatus(string processName)
    {
        _statuses.TryGetValue(NormalizeProcessName(processName), out var status);
        return status;
    }

    public void Scan()
    {
        Log.Write("Scan start");
        // Remove dead processes
        List<int> detachedPids = new();
        lock (_attachedLock)
        {
            var dead = _attached.Where(kv =>
            {
                try { return Process.GetProcessById(kv.Key).HasExited; }
                catch { return true; }
            }).Select(kv => kv.Key).ToList();

            foreach (int pid in dead)
            {
                Log.Write($"Process exited: pid={pid}, name={_attached[pid].Name}");
                _attached[pid].SharedMem.Dispose();
                _attached.Remove(pid);
                detachedPids.Add(pid);
            }
        }

        foreach (int pid in detachedPids)
            OnDetach?.Invoke(pid);

        // Build lookup of target process names (without .exe)
        var processNames = _settings.Processes
            .Select(NormalizeProcessName)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (processNames.Count == 0) return;

        var processes = Process.GetProcesses();
        foreach (string target in processNames)
        {
            var status = GetOrCreateStatus(target);
            status.MatchCount = 0;
            status.CandidatePid = null;
            status.CandidateName = "";
            status.CandidateWindowTitle = "";
            status.AttachedPid = null;
            status.AttachedName = "";
            status.AttachedWindowTitle = "";

            var candidates = processes
                .Select(proc => TryCreateCandidate(proc, target))
                .Where(candidate => candidate != null)
                .Select(candidate => candidate!)
                .OrderByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.WindowTitle))
                .ThenBy(candidate => candidate.StartTime ?? DateTime.MaxValue)
                .ThenBy(candidate => candidate.Pid)
                .ToList();

            status.MatchCount = candidates.Count;

            AttachedProcess? attachedForTarget;
            lock (_attachedLock)
            {
                attachedForTarget = _attached.Values
                    .FirstOrDefault(ap => ap.TargetName.Equals(target, StringComparison.OrdinalIgnoreCase));
            }
            if (attachedForTarget != null)
            {
                status.AttachedPid = attachedForTarget.Pid;
                status.AttachedName = attachedForTarget.Name;
                status.AttachedWindowTitle = attachedForTarget.WindowTitle;
                status.LastError = null;
                continue;
            }

            if (candidates.Count == 0)
            {
                status.LastError = null;
                continue;
            }

            var chosen = ChooseCandidate(target, candidates);
            status.CandidatePid = chosen.Pid;
            status.CandidateName = chosen.Name;
            status.CandidateWindowTitle = chosen.WindowTitle;

            lock (_attachedLock)
            {
                if (_attached.ContainsKey(chosen.Pid))
                    continue;
            }

            TryAttach(target, chosen, status);
        }
    }

    public void UpdateAllSpeeds(double speed, bool enabled)
    {
        List<AttachedProcess> attached;
        lock (_attachedLock)
            attached = _attached.Values.ToList();

        Log.Write($"UpdateAllSpeeds: speed={speed:F2}, enabled={enabled}, attached={attached.Count}");
        foreach (var ap in attached)
            ap.SharedMem.SetSpeed(speed, enabled);
    }

    public void DetachAll()
    {
        // CE behavior: never unhook, never unload the DLL.
        // Just set speed to 1.0 (passthrough) and release our shared memory handle.
        List<AttachedProcess> attached;
        lock (_attachedLock)
        {
            attached = _attached.Values.ToList();
            _attached.Clear();
        }

        foreach (var ap in attached)
        {
            ap.SharedMem.SetSpeed(1.0, false);
            ap.SharedMem.Dispose();
        }
    }

    private ProcessScanStatus GetOrCreateStatus(string target)
    {
        if (!_statuses.TryGetValue(target, out var status))
        {
            status = new ProcessScanStatus { TargetName = target };
            _statuses[target] = status;
        }
        return status;
    }

    private static string NormalizeProcessName(string processName)
    {
        string name = processName.Trim();
        if (string.IsNullOrEmpty(name)) return "";

        try
        {
            string fileName = Path.GetFileName(name);
            if (!string.IsNullOrWhiteSpace(fileName))
                name = fileName;
        }
        catch { }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private ProcessCandidate? TryCreateCandidate(Process proc, string target)
    {
        try
        {
            if (!proc.ProcessName.Equals(target, StringComparison.OrdinalIgnoreCase))
                return null;

            string title = "";
            DateTime? startTime = null;
            try { title = proc.MainWindowTitle ?? ""; } catch { }
            try { startTime = proc.StartTime; } catch { }

            return new ProcessCandidate(proc.Id, proc.ProcessName, title, startTime);
        }
        catch
        {
            return null;
        }
    }

    private ProcessCandidate ChooseCandidate(string target, List<ProcessCandidate> candidates)
    {
        if (_preferredPids.TryGetValue(target, out int preferredPid))
        {
            var preferred = candidates.FirstOrDefault(candidate => candidate.Pid == preferredPid);
            if (preferred != null)
                return preferred;

            _preferredPids.Remove(target);
        }

        if (candidates.Count > 1)
        {
            var chosen = candidates[0];
            Log.Write($"Multiple matches for {target}. Using first only: pid={chosen.Pid}, count={candidates.Count}");
        }

        return candidates[0];
    }

    private void TryAttach(string target, ProcessCandidate candidate, ProcessScanStatus status)
    {
        try
        {
            Log.Write($"Candidate process: pid={candidate.Pid}, name={candidate.Name}");
            var shm = new SharedMemory(candidate.Pid);
            try
            {
                Injector.Inject(candidate.Pid, _dllPath);
            }
            catch
            {
                Log.Write($"Injection failed: pid={candidate.Pid}, name={candidate.Name}");
                shm.Dispose();
                throw;
            }

            var ap = new AttachedProcess
            {
                Pid = candidate.Pid,
                TargetName = target,
                Name = candidate.Name,
                WindowTitle = candidate.WindowTitle,
                SharedMem = shm
            };
            lock (_attachedLock)
                _attached[candidate.Pid] = ap;
            status.AttachedPid = candidate.Pid;
            status.AttachedName = candidate.Name;
            status.AttachedWindowTitle = candidate.WindowTitle;
            status.LastError = null;
            Log.Write($"Attached: pid={candidate.Pid}, name={candidate.Name}");
            try
            {
                OnAttach?.Invoke(ap);
            }
            catch (Exception ex)
            {
                Log.Write($"Attach event handler error for pid={candidate.Pid}, name={candidate.Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            status.LastError = ex.Message;
            Log.Write($"Scan error for pid={candidate.Pid}, name={candidate.Name}: {ex.Message}");
        }
    }

    private sealed record ProcessCandidate(int Pid, string Name, string WindowTitle, DateTime? StartTime);
}
