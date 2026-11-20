// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: ComVisible(false)]

// The engine is internal in 1.0: only the MdfLibrary namespace is public. The test suites and the oracle tool
// reach the engine through these friend assemblies; MdfScope is the planned application consumer.
[assembly: InternalsVisibleTo("MdfLib.Tests")]
[assembly: InternalsVisibleTo("MdfLib.CompatibilityTests")]
[assembly: InternalsVisibleTo("MdfLib.Oracle")]
[assembly: InternalsVisibleTo("MdfScope")]
