# MdfLibrary.Managed

[![NuGet](https://img.shields.io/nuget/v/MdfLibrary.Managed)](https://www.nuget.org/packages/MdfLibrary.Managed)
[![CI](https://github.com/sensboston/mdflib.net/actions/workflows/ci.yml/badge.svg)](https://github.com/sensboston/mdflib.net/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-Framework%204.8%20%7C%208%20%7C%2010-512BD4)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

A fully managed .NET port of [mdflib](https://github.com/ihedvall/mdflib): reading and writing ASAM MDF 3.x and 4.x
measurement files, the format of automotive data loggers and calibration tools. The public API is the API of the
native `MdfLibrary` .NET wrapper of mdflib 2.3.0, class by class and member by member, so existing code compiles
against it unchanged. No C++/CLI, no P/Invoke, no native binaries, no `unsafe` code; zlib is managed as well.

```
dotnet add package MdfLibrary.Managed
```

## mdflib

mdflib is the work of **Ingemar Hedvall**: a complete, open C++ implementation of MDF, with a .NET wrapper, tools
and a viewer. The block layouts, the object model and the behaviour of every public member in this repository come
from it, and the native wrapper is the reference the CI compares against. Thank you, Ingemar, for mdflib and for
giving it away under MIT.

## Why a managed version

- **Runs wherever .NET runs.** The native wrapper is a C++/CLI assembly for Windows x64 only. This package is plain IL:
  Windows, Linux and macOS, x64 and ARM64, containers and cloud functions, .NET Framework 4.8 and modern .NET.
- **Drop-in.** Replace the reference to the native `MdfLibrary` with this package; the namespace, the types and their
  members are the same. The signatures are fixed in [`api/mdflibrary-api-baseline.txt`](api/mdflibrary-api-baseline.txt)
  and checked on every build.
- **Safe on damaged files.** Every read is bounds-checked, block counts, link depth, text sizes and buffers are capped,
  and a malformed file ends in an exception, never in a crash of the process.

## What it does

**Reading** (`MdfReader`): MDF 3.00–3.30 and 4.00–4.20 files. Sorted and unsorted data groups; DT, DZ (zlib, with
transposition), DL, HL and the 4.20 LD/DV/DI blocks. Integers of 1–64 bits, half/single/double floats, complex values,
text in every MDF encoding, byte arrays, MIME and CANopen values, VLSD and MLSD channels, fixed arrays and structures.
All conversions: linear, rational, polynomial, exponential, logarithmic, algebraic formulas, value and range tables,
text tables and bitfields. Header, file history, attachments (with MD5 check) and events. Samples are read through
`MdfChannelObserver`, as with the native wrapper.

**Writing** (`MdfWriter`): new MDF 3.30, 4.10 and 4.20 files (`Mdf3Basic`, `Mdf4Basic`). Master and data channels of
every fixed-size type, VLSD text and byte arrays, structures, conversions, sources, attachments, events, file history
and header metadata; optional zlib compression.

**Bus logging** (`MdfBusLogger` writer type): CAN and CAN FD frames, including remote, error and overload frames, in the
ASAM bus-logging layout, written with `SaveCanMessage` and read back like any other file.

## Example

```csharp
using System;
using MdfLibrary;

var reader = new MdfReader("measurement.mf4");
try
{
    if (!reader.ReadEverythingButData()) throw new InvalidOperationException("Not an MDF file.");
    foreach (MdfDataGroup dataGroup in reader.Header.DataGroups)
        foreach (MdfChannelGroup group in dataGroup.ChannelGroups)
        {
            Console.WriteLine($"{group.Name}: {group.NofSamples} samples");
            foreach (MdfChannel channel in group.Channels)
                Console.WriteLine($"  {channel.Name} [{channel.Unit}]");
        }
}
finally
{
    reader.Dispose();
}
```

More in [`samples`](samples): writing MDF 4 and MDF 3, observers, conversions, attachments, CAN bus logs, CSV export
and an `mdfinfo`-style dump. Each sample writes a file, reads it back and checks the values.

## MdfScope

[`apps/MdfScope`](apps/MdfScope) is a desktop viewer built on this library with [Avalonia](https://avaloniaui.net) and
[ScottPlot](https://scottplot.net), for Windows, Linux and macOS:

- open MDF 3 and MDF 4 files, plot any channels on panels with an axis per unit, read values at the cursor, see
  events on the time axis, export to CSV; CAN bus logs are decoded with a DBC file;
- capture a live CAN bus with a DBC: decoded signals in a tree with their current values, live plots, frame
  statistics, and the capture saved as an MDF 4 bus log;
- CAN adapters are plugins: PEAK PCAN (tested with hardware), SocketCAN on Linux (tested on `vcan`), SLCAN serial
  adapters (tested with an emulator) and Kvaser CANlib (not tested with hardware yet).

### Screenshots

<img width="1463" height="785" alt="Untitled-4" src="https://github.com/user-attachments/assets/2326890b-b674-4f54-9269-976364a3816d" />
<img width="2655" height="1763" alt="Untitled-3" src="https://github.com/user-attachments/assets/5a046ea1-93ae-4a20-a7fa-86d05b2ea1b4" />
<img width="3537" height="1928" alt="Untitled-2" src="https://github.com/user-attachments/assets/d462a180-7fd2-4f86-a96e-6eef3223dec8" />
<img width="3537" height="1928" alt="Untitled" src="https://github.com/user-attachments/assets/3bff00c5-b764-4527-ae28-027c8d80a48e" />

<!-- screenshots go here -->


Ready-to-run builds for Windows, Linux and macOS are attached to the [releases](https://github.com/sensboston/mdflib.net/releases).
On macOS the application is not signed: after unpacking, run `xattr -dr com.apple.quarantine MdfScope.app` once.

## Differences from the native wrapper

The managed writer does not yet cover everything the native one does; such members throw `NotSupportedException`
with the reason instead of writing a different file:

- channel arrays (`MdfChannelArray`) are read but not written; nested structures are not written;
- inverse conversions with texts or formulas, and the name, unit and comment of an inverse conversion;
- pre- and post-trigger times of events, periodic saving with the pre-trigger queue;
- the `MdfConverter` writer type, and bus logging for LIN, FlexRay, MOST and Ethernet (CAN only);
- XML metadata of channels, channel groups and conversions (only the header's common properties are written).

Writing channel arrays, nested structures and the remaining conversions is planned for 1.1.

## Tests

- Unit and compatibility tests on Windows (.NET Framework 4.8, .NET 8, .NET 10) and Linux (.NET 8, .NET 10).
- **Oracle:** the same scenarios are compiled against the native `MdfLibrary` 2.3.0 release and against this package;
  the files each one writes are read by both, and the dumps must match. A deliberately broken scenario checks that
  the comparison does catch differences.
- The package is audited on every build: managed code only, no dependencies, the public API equal to the baseline.

## License

MIT, see [LICENSE](LICENSE). mdflib is MIT as well; its notice and the notices of the test data are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
