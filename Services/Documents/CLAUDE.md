# Word documents (`Services/Documents/`)

Backs `DocumentFunctions` (`inspect_document`, `create_document`, `edit_document`, `convert_document`, `validate_document`); `DocxPackageService` derives from `Services/Ooxml/OoxmlPackageService`.

- Cross-run find/replace lives in `RunTextEditor`; styles and numbering in `DocxStyleLibrary`.
- `WordprocessingSchema` holds the ordered content models — **always add property elements through `SetProperty`**, since out-of-order children are what make Word offer to "repair" a file.
- The `docx` built-in skill only supplies routing and workflow discipline.
