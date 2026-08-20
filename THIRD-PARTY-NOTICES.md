# Third-party notices

inSANE's direct runtime dependencies are pinned in `Directory.Packages.props`.
They bring their own transitive dependencies; consult the resolved NuGet lock
data produced by `dotnet restore` for the complete set used by a specific build.

## NAPS2

- Packages: `NAPS2.Sdk`, `NAPS2.Images.ImageSharp`
- Version used by this repository: 1.3.0
- Project: <https://github.com/cyanfish/naps2>
- License: GNU General Public License, version 2 or (at your option) any later
  version

NAPS2 is the scanner, image-processing, import, and PDF-export foundation of
inSANE. Anyone distributing inSANE builds must review and comply with NAPS2's
GPL terms, including corresponding-source obligations. This repository does not
yet declare a project license; choose one compatible with those obligations
before public distribution.

## Six Labors ImageSharp

- Package: `SixLabors.ImageSharp`
- Version used by this repository: 3.1.11
- Project: <https://github.com/SixLabors/ImageSharp>

ImageSharp is used by the headless NAPS2 image context and by the optional demo
scanner. Review the package's license and current Six Labors licensing terms
before public or commercial distribution.
