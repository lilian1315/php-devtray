# Project notes

- Windows app: C# / .NET 10 / WPF.
- Build and checks: `powershell -File scripts/build.ps1`; add `-Publish` for release files.
- Read `docs/ARCHITECTURE.md` before changing runtime supervision or configuration.
- Closing/minimizing keeps services running in the tray; only Exit stops them.
- This app is unpublished; backward compatibility is not required.
- Keep generated builds, installed runtimes and local user data out of Git.
