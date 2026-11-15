using SharpDivert;

namespace OpenNetLimit.Engine.Interception;

/// <summary>
/// Thrown when a WinDivert handle can't be opened. Carries the layer, the flags and the
/// Windows error code so the failure can be explained to the user instead of surfacing
/// as a bare "The parameter is incorrect".
/// </summary>
public sealed class WinDivertOpenException : Exception
{
    public WinDivert.Layer Layer { get; }
    public WinDivert.Flag Flags { get; }
    public int NativeErrorCode { get; }

    /// <summary>The system's own text for <see cref="NativeErrorCode"/>.</summary>
    public string NativeMessage { get; }

    /// <summary>What a look at the PC found to be the cause, when it's known.</summary>
    public string? Detail { get; }

    /// <summary>Plain-language cause: <see cref="Detail"/>, else what <see cref="NativeErrorCode"/> usually means.</summary>
    public string Reason => Detail ?? DescribeError(NativeErrorCode);

    public WinDivertOpenException(WinDivert.Layer layer, WinDivert.Flag flags, WinDivertException inner)
        : this(layer, flags, inner.NativeErrorCode, inner.Message, inner)
    {
    }

    internal WinDivertOpenException(WinDivert.Layer layer, WinDivert.Flag flags, int nativeErrorCode, string nativeMessage,
        Exception? inner = null, string? detail = null)
        : base($"WinDivert could not open the {layer} layer (flags: {flags}): {nativeMessage} (Windows error {nativeErrorCode}). {detail ?? DescribeError(nativeErrorCode)}", inner)
    {
        Layer = layer;
        Flags = flags;
        NativeErrorCode = nativeErrorCode;
        NativeMessage = nativeMessage;
        Detail = detail;
    }

    /// <summary>The same failure, with the cause that was found.</summary>
    internal WinDivertOpenException WithDetail(string detail) =>
        new(Layer, Flags, NativeErrorCode, NativeMessage, InnerException, detail);

    /// <summary>
    /// Maps the WinDivertOpen error codes listed in the WinDivert 2.2 documentation to a
    /// cause the user can act on.
    /// </summary>
    public static string DescribeError(int nativeErrorCode) => nativeErrorCode switch
    {
        2 or 3 => "Windows couldn't find a WinDivert driver file. Antivirus may have quarantined WinDivert64.sys, or another program's WinDivert driver registration points at a folder that no longer exists.",
        5 => "The service isn't running with administrator rights.",
        87 => "WinDivert rejected the layer and flag combination.",
        577 => "Windows rejected the WinDivert driver signature. Secure Boot policy or Memory Integrity (HVCI) can cause this.",
        654 => "A different WinDivert version is already loaded. Restart Windows and try again.",
        1060 => "The WinDivert driver service couldn't be registered.",
        1275 => "Security software or Memory Integrity (HVCI) blocked the WinDivert driver.",
        1753 => "The Base Filtering Engine service is stopped or disabled.",
        _ => "WinDivert couldn't start."
    };
}
