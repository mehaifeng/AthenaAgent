# Styles and icons

- **Icons come from CoreUI Icons Free, never from Semi.** `Styles/CoreIcons.axaml` is
  generated — edit `Scripts/coreui-icons.manifest` and run
  `python3 Scripts/generate-coreui-icons.py`, never the .axaml by hand.
- Views and view models bind only to the semantic `AthenaIcon*` aliases in
  `Styles/AppIcons.axaml`; binding a `CoreIcon*` key directly defeats the indirection that
  makes the vendor swappable. Dynamic icon choices go through a key string plus
  `ToolIconKeyToGeometryConverter` (see `ToolCallDisplay.IconKey`,
  `WorkspaceFileIcons.ForFileName`).
- A missing `{StaticResource AthenaIconX}` neither fails the build nor throws — it silently
  leaves `PathIcon.Data` null and the icon disappears. `AssertEveryIconResolved` in the
  headless suite catches this on every captured window; do not weaken it.
- Attribution is required (CC BY 4.0): see `Docs/ThirdPartyNotices.md`.
