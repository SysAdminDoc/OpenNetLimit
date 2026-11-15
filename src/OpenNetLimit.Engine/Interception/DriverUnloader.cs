using System.ComponentModel;
using System.Diagnostics;

namespace OpenNetLimit.Engine.Interception;

/// <summary>
/// Unloads the shared <c>WinDivert</c> driver for a pause. Kernel anti-cheats refuse to start a
/// game while a packet-interception driver is loaded, even one no program is using, and
/// WinDivert stays loaded after its last handle closes until something stops it.
/// </summary>
public sealed class DriverUnloader
{
    /// <summary>
    /// How long the stop is watched. With no other program holding the driver it unloads within
    /// milliseconds; with one, it stays StopPending until that program closes it.
    /// </summary>
    public static readonly TimeSpan UnloadLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IDriverService _service;
    private readonly Action<TimeSpan, CancellationToken> _sleep;
    private readonly Func<IReadOnlyList<string>> _programsUsingWinDivert;

    public DriverUnloader(IDriverService service, Action<TimeSpan, CancellationToken>? sleep = null,
        Func<IReadOnlyList<string>>? programsUsingWinDivert = null)
    {
        _service = service;
        _sleep = sleep ?? DriverPreflight.Sleep;
        _programsUsingWinDivert = programsUsingWinDivert ?? ProgramsUsingWinDivert;
    }

    /// <summary>
    /// Stops the driver, which must no longer be open in this program, and waits for it to unload.
    /// </summary>
    /// <returns>Null once it's unloaded, or why it's still loaded and what to do.</returns>
    public string? Unload(CancellationToken cancellationToken)
    {
        if (IsUnloaded(TryQueryState(out _)))
            return null;

        string? stopError = null;
        try
        {
            _service.Stop();
        }
        catch (Win32Exception ex)
        {
            stopError = $"{ex.Message} (Windows error {ex.NativeErrorCode})";
        }

        var waited = TimeSpan.Zero;
        var state = TryQueryState(out string? queryError);
        while (stopError is null && !IsUnloaded(state) && waited < UnloadLimit)
        {
            _sleep(PollInterval, cancellationToken);
            waited += PollInterval;
            state = TryQueryState(out queryError);
        }
        if (IsUnloaded(state))
            return null;

        string? problem = stopError is not null ? $"Windows didn't stop it: {stopError}"
            : queryError is not null ? $"Its state couldn't be read: {queryError}"
            : null;
        return StillLoaded(state, problem, SafeProgramsUsingWinDivert());
    }

    /// <summary>Whether the driver is unloaded now. A stop left pending finishes on its own.</summary>
    public bool IsUnloaded() => IsUnloaded(TryQueryState(out _));

    /// <summary>What to tell the user when the driver is still loaded after a stop.</summary>
    internal static string StillLoaded(DriverServiceState? state, string? problem, IReadOnlyList<string> programs)
    {
        string holders = programs.Count switch
        {
            0 => "",
            1 => $" {programs[0]} has WinDivert loaded.",
            _ => $" These programs have WinDivert loaded: {string.Join(", ", programs)}."
        };
        if (problem is not null)
            return $"The WinDivert driver may still be loaded. {problem}.{holders} Restart Windows to be sure it's unloaded.";
        string closeWhat = programs.Count switch
        {
            0 => "the other program using it (a VPN, packet capture or another limiter)",
            1 => programs[0],
            _ => "them"
        };
        return state == DriverServiceState.StopPending
            ? $"The WinDivert driver is still loaded because another program has it open.{holders} " +
              $"It unloads by itself once you close {closeWhat}, or restart Windows."
            : $"The WinDivert driver is still loaded.{holders} Close {closeWhat} and pause again, or restart Windows.";
    }

    /// <summary>Whether a process with these modules loaded WinDivert, so it may hold the driver open.</summary>
    internal static bool LoadsWinDivert(IEnumerable<string> modulePaths) =>
        modulePaths.Any(path => Path.GetFileName(path).Equals("WinDivert.dll", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnloaded(DriverServiceState? state) =>
        state is DriverServiceState.Stopped or DriverServiceState.NotInstalled;

    private DriverServiceState? TryQueryState(out string? error)
    {
        try
        {
            error = null;
            return _service.QueryState();
        }
        catch (Win32Exception ex)
        {
            error = $"{ex.Message} (Windows error {ex.NativeErrorCode})";
            return null;
        }
    }

    private IReadOnlyList<string> SafeProgramsUsingWinDivert()
    {
        try
        {
            return _programsUsingWinDivert();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Listing the programs that use WinDivert failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Other running programs with WinDivert.dll loaded, by file name.</summary>
    private static IReadOnlyList<string> ProgramsUsingWinDivert()
    {
        int ownId = Environment.ProcessId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == ownId)
                    continue;
                try
                {
                    var paths = new List<string>();
                    foreach (ProcessModule module in process.Modules)
                    {
                        using (module)
                            paths.Add(module.FileName);
                    }
                    if (LoadsWinDivert(paths))
                        names.Add(process.ProcessName + ".exe");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected and exited processes can't be read. They're left out of the list.
                }
            }
        }
        return names.ToList();
    }
}
