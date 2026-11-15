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
