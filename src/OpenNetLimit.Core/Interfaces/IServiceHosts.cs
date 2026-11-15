namespace OpenNetLimit.Core.Interfaces;

/// <summary>
/// Which Windows services run in each process. Most svchost processes carry one service, but
/// Windows puts several in one on PCs with little memory, and a few always share.
/// </summary>
public interface IServiceHosts
{
    /// <summary>
    /// The services running in the process as of the last read, or null when none were, which
    /// includes a process started since. Never reads the service list, so the packet loop can
    /// call it.
    /// </summary>
    IReadOnlyList<string>? ServicesIn(uint processId);

    /// <summary>
    /// <see cref="ServicesIn"/>, but a svchost the last read didn't have gets the list read again
    /// first, at most about once a second.
    /// </summary>
    IReadOnlyList<string>? Resolve(uint processId, string processName);

    /// <summary>Reads the service list again, raising <see cref="Changed"/> when a process's services changed.</summary>
    void Refresh();

    /// <summary>A process's services changed since the last time this was raised.</summary>
    event Action? Changed;
}
