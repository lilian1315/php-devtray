# Desktop window behavior — 0.7.0

The manager is now a normal resizable desktop window, with native Windows title bar, minimize/maximize/close controls and a taskbar entry. It opens on ordinary launch and is centered initially. It is not always on top and does not hide when focus moves to another application. Escape no longer dismisses the manager.

Minimize and Close hide the existing window to the tray. Services and ongoing operations remain alive. Tray Open manager (and single-instance activation) restores the same window, preserving normal/maximized state and session geometry. Exit and stop services remains the explicit application shutdown command. The duplicate in-content close button was removed; the footer explains tray persistence.

Verification:
- Full Release build and configuration/process/UI resource checks passed. NuGet audit metadata warnings remain network-related.
- New opt-in `--smoke-window-lifecycle` checks normal native window properties, maximize → minimize → hidden → tray restore/maximized → close/hide → reopen. Passed against an isolated workspace profile.
- Actual normal-window client-content capture and accessible-name/text-contrast assertions passed in desktop-window-light.png. The WPF render captures client content, not native title-bar pixels.
- The resource-check host now explicitly suppresses application startup, so dispatcher-based tests do not open a user-facing manager window.

The lifecycle smoke used an isolated workspace profile. No OS settings were changed. Pointer-based desktop acceptance and multi-monitor geometry remain unverified.
