using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface IRateLimiter
{
    bool TryConsume(uint processId, int byteCount, bool isUpload);
    TimeSpan GetDelay(uint processId, int byteCount, bool isUpload);

    /// <summary>
    /// Charges a packet to the process's limit and gives how long it has to wait before it goes
    /// out. False means it would wait longer than <paramref name="maxDelay"/>: it was not charged
    /// and should be dropped. A process without a limit always gets true and no delay, a blocked
    /// one always gets false.
    /// </summary>
    bool TryReserve(uint processId, int byteCount, bool isUpload, TimeSpan maxDelay, out TimeSpan delay);

    /// <summary>
    /// Like the other overload, and with <paramref name="wholePc"/> also charges the whole-PC
    /// limit. The packet waits for whichever limit needs longer. When either would make it wait
    /// past <paramref name="maxDelay"/>, neither is charged.
    /// </summary>
    bool TryReserve(uint processId, int byteCount, bool isUpload, bool wholePc, TimeSpan maxDelay, out TimeSpan delay);

    /// <summary>
    /// Charges only the whole-PC limit, for traffic no app limit applies to. False means it would
    /// wait longer than <paramref name="maxDelay"/>: nothing was charged and it should be dropped.
    /// With no whole-PC limit that way it's always true and no delay.
    /// </summary>
    bool TryReserveWholePc(int byteCount, bool isUpload, TimeSpan maxDelay, out TimeSpan delay);

    /// <summary>The limit on the whole PC's traffic, or null when there's none.</summary>
    WholePcLimit? PcLimit { get; }

    /// <summary>Sets the whole-PC limit. Null, or 0 both ways, turns it off.</summary>
    void SetPcLimit(WholePcLimit? limit);

    /// <summary>The rates a process is limited to, 0 for a direction without a limit.</summary>
    bool TryGetLimit(uint processId, out long downloadBytesPerSecond, out long uploadBytesPerSecond);
    void SetLimit(uint processId, long downloadBytesPerSecond, long uploadBytesPerSecond);

    /// <summary>
    /// Drops all of the process's traffic until <see cref="SetLimit"/> or
    /// <see cref="RemoveLimit"/> replaces the block. A used-up quota with the Block action sets it.
    /// </summary>
    void Block(uint processId);
    bool IsBlocked(uint processId);
    void RemoveLimit(uint processId);
    void RemoveAll();
    bool HasLimit(uint processId);
}
