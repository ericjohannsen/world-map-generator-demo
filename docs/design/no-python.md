# No Python

**Status:** Policy. Applies to all new work and to any documentation added from now on.

WorldGen does **not** use Python for any purpose. It is not a runtime dependency, a build dependency, a test
dependency, or a supported development tool. Do not add Python scripts, packages, virtual environments,
`pip` instructions, or Python-based acceptance steps to this repository.

## Why

- The product is .NET/Avalonia and the pipeline is BCL-only ([NFR-2](../requirements/pipeline.md#non-functional-requirements)).
- A second interpreter adds an install step, a package manager, and a supply-chain surface for no benefit.
- The tasks that used Python — walking files, validating artifacts, generating a viewer — are covered by
  PowerShell and C#.

## What to use instead

| Need | Use |
| --- | --- |
| Concatenate the documentation | [`make-context.ps1`](../../make-context.ps1) |
| Validate a run | `dotnet run --project src/WorldGen.Cli -- --check`, plus the C# readers (`GeoTiffReader`, `PngReader` when added) |
| Inspect terrain in 3D | the in-app 3D preview (`src/WorldGen.App`, see [preview-3d.md](preview-3d.md)) |
| Rebuild an HTML globe | not supported; the in-app globe replaces it |

## Legacy files

`tools/make_globe.py`, `tools/verify_geotiff.py`, and `tools/verify_png.py` predate this policy. They are
**not** part of the build, test, or release path, are unsupported, and must not be used by new work. They are
scheduled for replacement by .NET equivalents. Documentation must label them as legacy rather than
recommend them.

Historical decision records (ADR-006, ADR-009) mention these validators as they existed at the time. Those
are records, not instructions; this policy supersedes them.

## Enforcement

- A pull request that adds a `.py` file, a Python package reference, or a Python command in documentation is
  rejected unless this policy is explicitly amended first.
- Scripts in the repository are PowerShell (`.ps1`) or C#. A batch file is acceptable only as a thin wrapper
  around PowerShell or the .NET CLI.
