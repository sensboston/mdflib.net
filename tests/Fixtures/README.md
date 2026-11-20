# Test fixtures

This folder holds the MDF 3 and MDF 4 files the test suites read, with `manifest.json` as their provenance record: `id`, `path`, `size`, `sha256`, MDF version, feature IDs, source, license, generator, expected content and limitations.

- `FixtureManifestTests` (`tests/MdfLib.CompatibilityTests`) checks every file's size and SHA-256 against the manifest; `verify-repository` checks the manifest's structure and that no undeclared file is present.
- The files are binary, all at most 256 KiB. The repository has no Python: the generators that once built them were removed, so changing a fixture means changing the file and its manifest entry together.
- Expected values for the oracle live in `tools/fixtures/*-expectations.json`; the history of each fixture is in `docs/progress.md`.
