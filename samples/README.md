# Samples

Eight console programs that show the public `MdfLibrary` API. Each one writes a small file to the temporary folder, reads it back,
checks the values and deletes the file. A mismatch makes the program exit with code 1.

| Sample | What it shows |
| --- | --- |
| `WriteMdf4` | Writing an MDF 4.10 file (master channel, unsigned counter) and reading the values back. |
| `ReadObservers` | Reading through channel observers; the number of samples is found by reading until a sample has no value. |
| `Conversions` | A rational function and a value-to-text table; reading the engineering values. |
| `Mdf3RoundTrip` | Writing and reading MDF 3.30, with big-endian and little-endian integers and the master unit `s`. |
| `HeaderAndAttachments` | A channel group description and an attachment reference in the header. |
| `CanLogger` | Writing CAN data frames with standard and extended identifiers into a bus log (`MdfWriterType.MdfBusLogger`), then reading the frame group back. |
| `ExportCsv` | Exporting every channel of a group to CSV; the column header is `name [unit]` (a channel without a unit has the plain name). |
| `ReadInfo` | File structure like `mdfinfo`: header metadata, data groups, channel groups with sample counts, channels with data type, unit and conversion. |

Rules for these programs:

- Only the public `MdfLibrary` API is used (no `MdfLib.*` namespaces, no internal types).
- Target `net8.0` with C# 8.0 (`LangVersion` set in each `.csproj`), so they override the repository's C# 7.3 for the library.
- Each project references `src/MdfLib/MdfLib.csproj` and has no package references. The repository contract requires lock files for every project, so restore writes `packages.lock.json` here; locked mode is off for samples, so a missing or outdated lock file does not fail the build.
- The samples are not in `MdfLib.sln`. The `samples` job in `.github/workflows/ci.yml` builds and runs every `samples/*/*.csproj`, and the required-checks job waits for it.

Run one locally (needs the .NET 8 SDK):

    dotnet run --project samples/WriteMdf4/WriteMdf4.csproj
