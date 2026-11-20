# Third-party notices

## mdflib

This library is a managed port of [ihedvall/mdflib](https://github.com/ihedvall/mdflib), revision
[`3f16741d55f3299d0c9575993c42a15e48b5db24`](https://github.com/ihedvall/mdflib/tree/3f16741d55f3299d0c9575993c42a15e48b5db24)
(release 2.3.0). The block layouts, the object model and the public API of its `MdfLibrary` .NET wrapper are adapted
from it. Its license at that revision:

```text
MIT License

Copyright (c) 2022 Ingemar Hedvall

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The zlib inflater and deflater are original managed code written from RFC 1950 and RFC 1951.

## Test data

The measurement files under `tests/Fixtures` contain synthetic data only and are covered by this repository's MIT
license. Some of them were written with [asammdf](https://github.com/danielhrisca/asammdf) 8.7.2 (LGPL-3.0-or-later)
as an independent producer, and some were cross-checked with asammdf and
[mdfreader](https://github.com/ratal/mdfreader) 4.8; no code of either is copied, linked or shipped. The origin and
SHA-256 of every file are listed in `tests/Fixtures/manifest.json`, the reference results in `tools/fixtures`.

## Build and test tools

Not shipped in the package: xUnit (Apache-2.0), Microsoft.NET.Test.Sdk and Microsoft.NETFramework.ReferenceAssemblies
(MIT). The CI downloads the native `mdflibrary.zip` of mdflib 2.3.0 for the oracle job and does not redistribute it.

## MdfScope

The viewer in `apps/` is not part of the package. It uses [Avalonia](https://github.com/AvaloniaUI/Avalonia) (MIT),
[ScottPlot](https://github.com/ScottPlot/ScottPlot) (MIT), [DbcParserLib](https://github.com/EFeru/DbcParserLib) (MIT),
[SkiaSharp](https://github.com/mono/SkiaSharp) (MIT) and System.IO.Ports (MIT). The CAN adapter plugins call the
vendors' own drivers (PEAK PCAN-Basic, Kvaser CANlib) when they are installed; those drivers are not distributed here.
