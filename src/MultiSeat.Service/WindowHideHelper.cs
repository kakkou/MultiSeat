using System.Diagnostics;
using MultiSeat.Service.Interop;

namespace MultiSeat.Service;

/// <summary>
/// Hides all top-level windows belonging to a process (used to hide the mstsc
/// window that keeps a seat's RDP session Active).
///
/// Must run IN the target Windows session (Session 1 / console session) because
/// EnumWindows only enumerates windows on the caller's window station/desktop.
/// A SYSTEM service in Session 0 (WinSta0\Default of Session 0) cannot see the
/// console user's mstsc window, so hiding it from the service is a silent no-op —
/// the window stays visible to whoever is using the host account (GitHub issue #8).
///
/// Invoked by the service via RunInConsoleSession (CreateProcessAsUser) so the
/// process runs in the console session where mstsc's window actually lives.
/// </summary>
internal static class WindowHideHelper
{
    /// <summary>
    /// The mstsc window that actually covers the screen. Hiding by class rather than hiding
    /// everything the process owns matters: mstsc's security/trust prompt is a plain dialog
    /// (<c>#32770</c>), and hiding that would leave an invisible modal nobody can answer —
    /// the dismisser could not click it and the connection would hang until it timed out.
    /// </summary>
    private const string RdpClientWindowClass = "TscShellContainerClass";

    /// <summary>
    /// Watch for an mstsc that starts AFTER this call and keep its RDP client window hidden.
    ///
    /// Exists because starting the watcher with a PID is inherently too late. mstsc creates its
    /// window hidden and then shows it itself about 300ms after launch, while spawning this
    /// helper (CreateProcessAsUser plus .NET startup) takes longer than that — so a PID-based
    /// watcher always arrives after the window is already on screen, measured at about a second
    /// of full-size window on the console user's display. Started BEFORE mstsc, the helper is
    /// already polling when that window appears and hides it within one poll interval.
    ///
    /// Only the first matching process is adopted. Once that process has been claimed, later
    /// mstsc processes are ignored so a normal Remote Desktop connection started by the console
    /// user cannot be hidden by a seat's long-lived watcher.
    /// </summary>
    /// <param name="processName">Process to watch for, without extension (i.e. "mstsc").</param>
    /// <param name="startedAfterUtc">
    /// Only processes started after this instant are touched. The caller stamps it immediately
    /// before launching mstsc.
    ///
    /// This is a timestamp rather than "processes running when I started" for a reason: taking
    /// that snapshot here loses a race it cannot win. The helper is spawned before mstsc, but its
    /// own runtime takes a few hundred milliseconds to boot, by which time mstsc already exists —
    /// so it lands in the snapshot, is treated as pre-existing, and is ignored for the rest of the
    /// seat's life. Measured exactly that way: the watcher ran, adopted nothing, and the window
    /// stayed on screen until an unrelated one-shot hid it seconds later.
    /// </param>
    /// <param name="adoptTimeoutSeconds">
    /// How long to wait for that new process to appear before giving up. Once one is adopted the
    /// helper runs until it exits, however long that is.
    /// </param>
    public static bool WatchAndHideNew(string processName, DateTime startedAfterUtc, int adoptTimeoutSeconds)
    {
        int? adoptedPid = null;
        var adoptDeadline = DateTime.UtcNow.AddSeconds(adoptTimeoutSeconds);
        var hidden = 0;

        Console.Out.WriteLine(
            $"[WindowHide] Watching for a {processName} started after {startedAfterUtc:HH:mm:ss.fff}Z");

        while (true)
        {
            var current = CurrentPids(processName);

            var previousAdoptedPid = adoptedPid;
            adoptedPid = SelectAdoptedPid(
                adoptedPid, current, pid => StartedAfter(pid, startedAfterUtc));

            if (previousAdoptedPid is null && adoptedPid is { } newlyAdoptedPid)
                Console.Out.WriteLine(
                    $"[WindowHide] Adopted new {processName} PID {newlyAdoptedPid}");

            if (adoptedPid is { } pid && current.Contains(pid))
            {
                foreach (var hWnd in FindVisibleClientWindows((uint)pid))
                {
                    User32.ShowWindow(hWnd, Kernel32.SW_HIDE);
                    hidden++;
                    Console.Out.WriteLine(
                        $"[WindowHide] Hid a visible {RdpClientWindowClass} window for PID {pid} (total {hidden})");
                }
            }

            // Once a process is adopted, follow ONLY that process until it exits. In particular,
            // do not adopt an unrelated mstsc the user starts later for a normal RDP connection.
            if (adoptedPid is { } adopted && !current.Contains(adopted)) break;

            // Give up only if no candidate ever appeared.
            if (adoptedPid is null && DateTime.UtcNow > adoptDeadline)
            {
                Console.Out.WriteLine($"[WindowHide] No new {processName} appeared within {adoptTimeoutSeconds}s");
                break;
            }

            Thread.Sleep(100);
        }

        Console.Out.WriteLine($"[WindowHide] Stopped watching after hiding {hidden} window(s)");
        return true;
    }

    /// <summary>
    /// Pick the first eligible process and keep that choice stable for the lifetime of the
    /// watcher. A seat watcher must never switch to a later mstsc, because that later process
    /// may be a normal Remote Desktop connection the console user started themselves.
    /// </summary>
    internal static int? SelectAdoptedPid(
        int? adoptedPid, IEnumerable<int> currentPids, Func<int, bool> isEligible)
    {
        if (adoptedPid is not null)
            return adoptedPid;

        foreach (var pid in currentPids)
        {
            if (isEligible(pid))
                return pid;
        }

        return null;
    }

    private static HashSet<int> CurrentPids(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// True when the process began after the given instant. Anything older is someone else's —
    /// an mstsc the user opened themselves must never be hidden.
    /// </summary>
    private static bool StartedAfter(int pid, DateTime startedAfterUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime() >= startedAfterUtc;
        }
        catch
        {
            // Exited, or start time unreadable — either way, not ours to touch.
            return false;
        }
    }

    /// <summary>
    /// Keep the RDP client window hidden for as long as the process lives (or for a bounded
    /// number of seconds).
    ///
    /// A single hide after connecting is not enough. mstsc shows its window again later — on
    /// connect, on reconnect, and when the session's resolution changes — and on a host whose
    /// console someone is actually using, that window covers their screen. Closing it (the only
    /// obvious response) disconnects the seat. So watch instead of hiding once.
    ///
    /// Deliberately narrow: only <see cref="RdpClientWindowClass"/> is touched, and only when it
    /// is genuinely visible and not minimized, so this can never swallow a dialog.
    /// </summary>
    /// <param name="pid">mstsc process to watch.</param>
    /// <param name="seconds">-1 to watch for the process's lifetime; otherwise a bound.</param>
    public static bool WatchAndHide(int pid, int seconds)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine($"[WindowHide] PID {pid} not found in this session");
            return false;
        }

        var deadline = seconds < 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
        var hidden = 0;

        Console.Out.WriteLine(
            $"[WindowHide] Watching PID {pid} ({(seconds < 0 ? "for its lifetime" : seconds + "s")})");

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (process.HasExited) break;
            }
            catch { break; }

            foreach (var hWnd in FindVisibleClientWindows((uint)pid))
            {
                User32.ShowWindow(hWnd, Kernel32.SW_HIDE);
                hidden++;
                Console.Out.WriteLine(
                    $"[WindowHide] Hid a visible {RdpClientWindowClass} window for PID {pid} (total {hidden})");
            }

            // 100ms rather than 250: this is racing mstsc showing its own window, and every
            // poll interval is time that window spends on someone's screen.
            Thread.Sleep(100);
        }

        Console.Out.WriteLine($"[WindowHide] Stopped watching PID {pid} after hiding {hidden} window(s)");
        return true;
    }

    /// <summary>
    /// Visible, non-minimized RDP client windows owned by the process. Minimized ones are left
    /// alone — they are already off-screen, and mstsc is started minimized on purpose.
    /// </summary>
    private static List<IntPtr> FindVisibleClientWindows(uint targetPid)
    {
        var matches = new List<IntPtr>();

        User32.EnumWindows((hWnd, _) =>
        {
            User32.GetWindowThreadProcessId(hWnd, out var wndPid);
            if (wndPid != targetPid) return true;
            if (!User32.IsWindowVisible(hWnd) || User32.IsIconic(hWnd)) return true;
            if (GetClassName(hWnd) != RdpClientWindowClass) return true;

            matches.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        return matches;
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        var length = User32.GetClassName(hWnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>
    /// Hide every top-level window owned by the given PID. Returns true if the
    /// process was found (whether or not it had visible windows).
    /// </summary>
    public static bool HideByPid(int pid)
    {
        try
        {
            // Verify the target process exists in this session before enumerating.
            try { using var _ = Process.GetProcessById(pid); }
            catch (ArgumentException)
            {
                Console.Error.WriteLine($"[WindowHide] PID {pid} not found in this session");
                return false;
            }

            var targetPid = (uint)pid;
            var hid = 0;
            User32.EnumWindows((hWnd, _) =>
            {
                User32.GetWindowThreadProcessId(hWnd, out var wndPid);
                if (wndPid == targetPid)
                {
                    User32.ShowWindow(hWnd, Kernel32.SW_HIDE);
                    hid++;
                }
                return true; // continue enumeration
            }, IntPtr.Zero);

            Console.Out.WriteLine($"[WindowHide] Hid {hid} window(s) for PID {pid}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WindowHide] Failed for PID {pid}: {ex.Message}");
            return false;
        }
    }
}
