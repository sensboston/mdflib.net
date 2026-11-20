using System.Diagnostics;
using System.Runtime.CompilerServices;
using MdfScope.Can;

namespace MdfScope;

/// <summary>
/// A built-in simulated bus: a vehicle that drives a speed profile and sends a few frames, so capture,
/// recording and decoding can be tried without hardware. The layout matches <c>Demo/demo-vehicle.dbc</c>.
/// </summary>
public sealed class DemoBusProvider : ICanAdapterProvider
{
    public string Name => "Demo bus";
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public IReadOnlyList<CanDevice> GetDevices() => [new CanDevice("demo", "Simulated vehicle (no hardware)", SupportsFd: false)];
    public ICanChannel Open(CanDevice device, CanChannelOptions options) => new DemoBusChannel(device);
}

sealed class DemoBusChannel(CanDevice device) : ICanChannel
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    bool closed;

    public CanDevice Device => device;
    public string BusState => closed ? "closed" : "OK";
    public bool CanSend => false;
    public void Send(CanFrame frame) => throw new NotSupportedException("The demo bus is receive-only.");
    public void Dispose() => closed = true;

    public async IAsyncEnumerable<CanFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long tick = 0;
        var rng = new Random(3);
        double speed = 0, pedal = 0;
        while (!closed && !cancellationToken.IsCancellationRequested)
        {
            // 100 Hz base rate; frames are produced in bursts per 10 ms tick
            long due = tick * 10;
            long now = clock.ElapsedMilliseconds;
            if (due > now) await Task.Delay((int)(due - now), cancellationToken).ConfigureAwait(false);
            double t = tick / 100.0;
            ulong ns = (ulong)(tick * 10_000_000L);

            double target = 60 + 45 * Math.Sin(t / 9) + 20 * Math.Sin(t / 2.3);
            double accel = Math.Clamp((target - speed) * 0.8, -6, 3);
            speed = Math.Max(0, speed + accel * 0.036);
            pedal += ((accel > 0 ? 15 + accel * 22 : 0) - pedal) * 0.08;
            int gear = speed < 1 ? 0 : speed < 15 ? 1 : speed < 30 ? 2 : speed < 48 ? 3 : speed < 68 ? 4 : speed < 92 ? 5 : 6;
            double[] ratio = [0, 13.2, 8.1, 5.6, 4.3, 3.5, 2.9];
            double rpm = gear == 0 ? 820 : Math.Max(820, speed / 3.6 / (2 * Math.PI * 0.31) * 60 * ratio[gear]) + rng.Next(20);

            // 0x100 EngineData: EngineSpeed (0.25 rpm/bit), VehicleSpeed (0.01 km/h/bit), AcceleratorPedal (0.4 %/bit)
            var d100 = new byte[8];
            Le16(d100, 0, (int)(rpm / 0.25));
            Le16(d100, 2, (int)(speed / 0.01));
            d100[4] = (byte)Math.Clamp(pedal / 0.4, 0, 250);
            d100[5] = (byte)(gear | (accel < -1 ? 0x10 : 0));
            d100[6] = (byte)(tick & 0x0F);
            yield return new CanFrame(ns, 0x100, false, CanFrameKind.Data, d100, 8);

            if (tick % 2 == 0)
            {
                // 0x180 Chassis: SteeringAngle (0.1 deg/bit, signed), LateralAccel (0.01 m/s²/bit, signed)
                double steer = 35 * Math.Sin(t / 3.1) + rng.NextDouble();
                double lat = Math.Pow(speed / 3.6, 2) * steer / 4200;
                var d180 = new byte[4];
                Le16(d180, 0, (int)(steer / 0.1));
                Le16(d180, 2, (int)(lat / 0.01));
                yield return new CanFrame(ns + 120_000, 0x180, false, CanFrameKind.Data, d180, 4);
            }

            if (tick % 10 == 0)
            {
                // 0x18FEEE00 (extended) Temperatures: Coolant (°C, offset -40), Oil (°C, offset -40), Battery (0.05 V/bit)
                var dT = new byte[4];
                dT[0] = (byte)(40 + 82 + 6 * Math.Sin(t / 40));
                dT[1] = (byte)(40 + 95 + 4 * Math.Sin(t / 55));
                Le16(dT, 2, (int)((13.9 + 0.2 * Math.Sin(t)) / 0.05));
                yield return new CanFrame(ns + 340_000, 0x18FEEE00, true, CanFrameKind.Data, dT, 4);
            }
            tick++;
        }
    }

    static void Le16(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
}
