# Third-party notices — bundled assets

This file covers third-party **assets that are copied into the repository and shipped inside
the application binary**. Code dependencies are consumed as NuGet packages and carry their own
licence metadata inside each package; they are not restated here.

## CoreUI Icons Free

- **Used for:** every icon in the application UI (`Styles/CoreIcons.axaml`, generated from
  `Scripts/coreui-icons.manifest` by `Scripts/generate-coreui-icons.py`).
- **Upstream:** https://github.com/coreui/coreui-icons
- **Copyright:** © creativeLabs Łukasz Holeczek
- **Licence (SVG icons):** Creative Commons Attribution 4.0 International (CC BY 4.0) —
  https://creativecommons.org/licenses/by/4.0/

CC BY 4.0 requires attribution, which is why this notice exists: the icon geometries are
redistributed inside the application, not merely referenced at build time. The generated
`Styles/CoreIcons.axaml` carries the same attribution in its header comment; do not strip it.

The geometries are transformed (SVG `<path>` elements merged into a single nonzero-fill
`StreamGeometry`, coordinates re-serialised, and two icons composed with a `+` badge). CC BY 4.0
permits such adaptation; this notice records that the shipped artwork is modified.

**Brand marks.** `cib-github` is distributed by CoreUI under CC0, but the GitHub logo remains a
trademark of GitHub, Inc. It is used solely to label a link to this project's GitHub repository.
Do not reuse brand marks for anything other than referring to the product they depict.

## Athena's own artwork

The owl mark (`AthenaIconSubAgentsOwl` in `Styles/AppIcons.axaml`), the application icons under
`Assets/`, and the pet sprites are Athena's own and are covered by this repository's licence.
