# Changelog

All notable changes to this project are documented here.

## [Unreleased]

### Added
- Pack Folder can now pack a loose folder straight into IoStore (`.utoc`/`.ucas`) format, not just a legacy `.pak`.
- Repack to IoStore now works from an open legacy `.pak`, not just a loose-folder workspace.
- A standalone "Convert IoStore to Legacy..." dialog (Tools menu), converting a `.utoc` container to either a loose folder or a `.pak`.
- `.github/dependabot.yml` for NuGet dependency updates.
- CI: every push/PR builds, tests, and runs analyzers.

### Fixed
- A real retoc `to-zen` failure ("Failed to get Package Path from Content/...") for UE4.27-era containers, caused by retoc needing a project-name folder segment in the walked path.
- The results grid's columns reflowing (and visibly scroll-jumping) on every value edit.
- `EmbeddedToolLocator` re-reading and SHA-256-hashing the embedded retoc/worker payload on every call instead of only once.
- Unhandled exceptions and unobserved task failures now get logged to a persistent file instead of vanishing when the window closes.

### Changed
- Test suite migrated from xUnit v2 + VSTest to xUnit v3 on Microsoft.Testing.Platform.
- Release builds now treat warnings as errors.

[Unreleased]: https://github.com/gravenuance/UAssetEditor/commits/main
