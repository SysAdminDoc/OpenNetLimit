namespace OpenNetLimit.Core.Interfaces;

/// <summary>
/// Which Windows services run in each process. Most svchost processes carry one service, but
/// Windows puts several in one on PCs with little memory, and a few always share.
/// </summary>
public interface IServiceHosts
{
    /// <summary>
    /// The services running in the process as of the last read, or null when none were, which
    /// includes a process started since and one that has exited. Never reads the service list,
    /// so the packet loop can call it.
    /// </summary>
    IReadOnlyList<string>? ServicesIn(uint processId);

    /// <summary>
    /// <see cref="ServicesIn"/>, and a svchost the last read didn't have gets the list read again
    /// on another thread, at most about once a second, which raises <see cref="Changed"/> when it
    /// finds the svchost. Never waits for that read.
    /// </summary>
    IReadOnlyList<string>? Resolve(uint processId, string processName);

    /// <summary>Reads the service list again, raising <see cref="Changed"/> when a process's services changed.</summary>
    void Refresh();

    /// <summary>
    /// A process's services changed, or a process that had services exited. Raised for an exit
    /// before its id can go to another process.
    /// </summary>
    event Action? Changed;
}
