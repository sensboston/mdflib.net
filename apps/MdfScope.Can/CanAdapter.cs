namespace MdfScope.Can;

/// <summary>
/// Entry point of a CAN adapter plugin. MdfScope loads every assembly in its <c>plugins</c> folder and
/// creates each public, non-abstract type that implements this interface through its parameterless
/// constructor. A provider covers one adapter family (one vendor SDK or one operating-system interface).
/// </summary>
public interface ICanAdapterProvider
{
    /// <summary>Short name shown in the adapter list, e.g. "PEAK PCAN" or "SocketCAN".</summary>
    string Name { get; }

    /// <summary>
    /// Whether the provider can work on this machine at all (driver or library present). A provider that
    /// returns false is listed with <see cref="UnavailableReason"/> instead of devices.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Why the provider is not available, for the user; null when it is.</summary>
    string? UnavailableReason { get; }

    /// <summary>The channels currently attached. Called on demand; must not throw for "no devices".</summary>
    IReadOnlyList<CanDevice> GetDevices();

    /// <summary>Opens a channel for receiving (and, if the adapter can, sending).</summary>
    /// <exception cref="CanAdapterException">The channel cannot be opened with these settings.</exception>
    ICanChannel Open(CanDevice device, CanChannelOptions options);
}

/// <summary>One adapter channel as the provider reports it.</summary>
/// <param name="Id">Provider-specific identifier passed back to <see cref="ICanAdapterProvider.Open"/>.</param>
/// <param name="DisplayName">Name for the user, e.g. "PCAN-USB 1 (device 0x51)".</param>
/// <param name="SupportsFd">The channel can do CAN FD.</param>
/// <param name="IsInUse">Another program has the channel open, if the provider can tell.</param>
public sealed record CanDevice(string Id, string DisplayName, bool SupportsFd, bool IsInUse = false);

/// <summary>How to open a channel.</summary>
/// <param name="Bitrate">Nominal (arbitration) bit rate in bit/s, e.g. 500000.</param>
/// <param name="ListenOnly">
/// Receive without taking part in the bus: no acknowledge bits, no error frames. Use it on a bus that
/// already has other nodes; with a single node the frames stay unacknowledged and are repeated forever.
/// </param>
/// <param name="DataBitrate">CAN FD data-phase bit rate in bit/s; null for classic CAN.</param>
public sealed record CanChannelOptions(int Bitrate, bool ListenOnly = false, int? DataBitrate = null);

/// <summary>An open channel. Frames are read by one consumer; <see cref="IDisposable.Dispose"/> closes the channel.</summary>
public interface ICanChannel : IDisposable
{
    /// <summary>The device this channel was opened on.</summary>
    CanDevice Device { get; }

    /// <summary>
    /// Waits for frames and returns them in arrival order until <paramref name="cancellationToken"/> is
    /// cancelled or the channel is closed. Timestamps come from the adapter clock where it has one.
    /// </summary>
    IAsyncEnumerable<CanFrame> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>Bus state as the controller reports it, e.g. "OK", "error passive", "bus off".</summary>
    string BusState { get; }

    /// <summary>Whether <see cref="Send"/> is supported by this channel.</summary>
    bool CanSend { get; }

    /// <summary>Queues a frame for transmission.</summary>
    /// <exception cref="NotSupportedException">The channel is receive-only (see <see cref="CanSend"/>).</exception>
    void Send(CanFrame frame);
}

/// <summary>Kind of a received frame.</summary>
public enum CanFrameKind
{
    /// <summary>Data frame (classic or FD).</summary>
    Data,
    /// <summary>Remote transmission request.</summary>
    Remote,
    /// <summary>Error frame reported by the controller.</summary>
    Error,
}

/// <summary>A CAN or CAN FD frame.</summary>
/// <param name="TimestampNs">Time stamp in nanoseconds of the adapter clock (monotonic; zero point is the adapter's).</param>
/// <param name="Id">11- or 29-bit identifier.</param>
/// <param name="IsExtended">29-bit identifier.</param>
/// <param name="Kind">Data, remote or error frame.</param>
/// <param name="Data">Payload; for a remote frame empty with <paramref name="Dlc"/> giving the requested length.</param>
/// <param name="Dlc">Data length code as on the bus (0–8 classic, 0–15 FD).</param>
/// <param name="IsFd">CAN FD frame.</param>
/// <param name="BitRateSwitch">CAN FD frame sent with a faster data phase.</param>
/// <param name="IsTransmitted">Sent by this channel rather than received.</param>
public readonly record struct CanFrame(
    ulong TimestampNs, uint Id, bool IsExtended, CanFrameKind Kind, byte[] Data, byte Dlc,
    bool IsFd = false, bool BitRateSwitch = false, bool IsTransmitted = false)
{
    /// <summary>Payload length for a CAN FD data length code.</summary>
    public static int FdLength(int dlc) => dlc <= 8 ? dlc : dlc switch
    {
        9 => 12, 10 => 16, 11 => 20, 12 => 24, 13 => 32, 14 => 48, _ => 64,
    };
}

/// <summary>An adapter operation failed; the message is meant for the user.</summary>
public sealed class CanAdapterException(string message, Exception? inner = null) : Exception(message, inner);
