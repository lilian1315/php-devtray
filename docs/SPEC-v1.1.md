# PHP DevTray

## Functional and Technical Specification

**Version:** 1.1  
**Status:** Proposed implementation baseline  
**Platform:** Windows 10/11, x64  
**Technology:** C# / .NET 10 / WPF with Fluent styling  
**Distribution:** Self-contained Windows application

---

# 1. Executive Summary

PHP DevTray is a lightweight Windows system tray application for managing a local PHP and Nginx development environment.

It provides:

- Installation and updating of PHP and Nginx.
- Simultaneous execution of multiple PHP versions.
- A selectable default PHP version available through the Windows user PATH.
- Independent PHP configuration and extension management.
- Per-site PHP version selection.
- User-owned Nginx virtual host configurations.
- Integrated local SMTP email capture using Mailpit.
- Start, stop, restart, and status monitoring.
- A modern Fluent-style tray popup.
- Complete cleanup of managed processes when the application exits.

The application must not require Docker, IIS, Apache, WSL, or a permanently installed Windows Service.

**Primary design principle:** Runtime binaries are replaceable, user configurations are persistent, and all managed processes belong to the application's lifecycle.

# 2. Product Goals

## 2.1 Primary goals

1. Provide a lightweight alternative to full local development stacks.
2. Support several PHP versions running simultaneously.
3. Allow individual Nginx sites to select their PHP runtime.
4. Provide a default PHP CLI version accessible through PATH.
5. Keep user configuration separate from installed runtime files.
6. Make PHP extensions easy to enable and disable.
7. Provide an integrated SMTP mail trap for development.
8. Avoid requiring administrator privileges for normal operation.
9. Keep all management accessible from the Windows system tray.
10. Support installation and updates without losing user settings.
11. Ensure all managed services stop when the application exits.

## 2.2 Non-goals for version 1.0

The initial release will not include:

- Apache or IIS management.
- MySQL, PostgreSQL, Redis, or other database services.
- Docker or container orchestration.
- Production server management.
- Remote server administration.
- Automatic HTTPS certificate provisioning.
- A public SMTP relay.
- An application marketplace.
- Multi-user or remote administration.
- Advanced support for externally managed PHP FastCGI blocks in custom Nginx configurations.

These capabilities may be considered in future releases.

# 3. User Experience

## 3.1 Application model

PHP DevTray runs as a single-instance Windows application.

It has no conventional main window.

A tray icon remains visible while the application is running. Clicking the icon opens a compact Fluent-style popup positioned near the Windows notification area.

The popup must:

- Open on left-click.
- Close when focus moves elsewhere, except when a native file picker or external editor is intentionally launched.
- Support light and dark Windows themes.
- Respect Windows display scaling.
- Work with multiple monitors.
- Remain within the visible screen boundaries.
- Remember the last selected section.
- Support keyboard navigation.
- Avoid showing a taskbar button.

Right-clicking the tray icon opens a native context menu with exactly three essential commands:

- Open Manager
- Start All / Stop All
- Exit Application

The Start All / Stop All command must dynamically display the appropriate action based on the current state of the managed environment. If services are partially running or transitioning, the command should use the safest applicable action or be temporarily disabled.

All application management is available through these two tray interactions.

External editors, Windows Explorer, and the user's default browser may be launched for editing or inspecting resources.

## 3.2 Primary popup layout

The popup contains:

**Header**

- Application icon and name.
- Overall environment status.
- Settings shortcut.
- Close control.

**Service summary**

Three compact service cards:

1. Nginx
2. PHP Runtimes
3. Mail Trap

Each card shows its current status and provides appropriate controls.

**Navigation**

- PHP Versions
- Nginx Sites
- Mail Trap
- Logs
- Updates
- Settings

**PHP Versions**

Displays all installed PHP runtimes, their state, FastCGI endpoint, assigned site count, and controls.

The default PHP version must be clearly identified and configurable.

**Nginx Sites**

Displays configured sites, document roots, enabled state, and selected PHP version.

**Mail Trap**

Displays captured messages and service controls.

## 3.3 Native context menu

The native tray context menu provides exactly three top-level commands:

- Open Manager
- Start All / Stop All
- Exit Application

The command labels should be updated dynamically:

- Use `Start All` when all managed services are stopped.
- Use `Stop All` when any managed service is running or active.
- Use the safest applicable label or disable the command while services are transitioning or in an unrecoverable state.

Detailed management actions remain available inside the popup, including:

- Individual PHP runtime controls.
- Default PHP version selection.
- Nginx and site controls.
- Mail Trap controls.
- Configuration and log access.
- Update management.
- Settings.

## 3.4 Tray icon states

The tray icon must support the following visual states:

| State | Icon behavior |
| --- | --- |
| All required services healthy | Green |
| All services stopped | Gray |
| One or more services transitioning | Animated spinner |
| Partial service failure | Amber |
| Critical startup or configuration failure | Red |

The spinner uses approximately 8–12 pre-rendered frames, advancing every 80–120 milliseconds.

A single centralized state manager controls the icon.

If multiple operations are active, animation continues until all tracked operations have completed.

The tooltip should summarize the environment, for example:

`Nginx running · PHP: 3 active · Mailpit running`

The implementation must respect Windows notification icon tooltip length limits.

# 4. Application Architecture

## 4.1 Technology stack

| Component | Technology |
| --- | --- |
| Language | C# |
| Runtime | .NET 10 |
| UI | WPF with Fluent styling |
| Tray integration | Win32 NotifyIcon interop or a maintained tray library |
| Process management | System.Diagnostics.Process |
| Process containment | Windows Job Objects |
| Configuration | JSON and INI files |
| Persistent application state | JSON |
| Email capture | Mailpit |
| HTTP communication | HttpClient |
| Logging | Microsoft.Extensions.Logging with structured file logging |
| Dependency injection | Microsoft.Extensions.DependencyInjection |
| Installer | WiX or another maintained Windows installer framework |

WPF is responsible for the custom tray popup.

The runtime management layer must remain independent of the UI so that it can be tested without starting WPF.

## 4.2 Logical modules

The application consists of:

**Tray Host**

- Notification icon
- Context menu
- Popup positioning
- Window activation
- UI state

**Runtime Supervisor**

- Process ownership
- Startup and shutdown
- Health monitoring
- Crash detection
- Job Object management

**PHP Manager**

- Version discovery
- Installation
- Runtime startup
- FastCGI endpoint allocation
- Default CLI version and PATH integration
- Configuration
- Extensions
- Updates

**Nginx Manager**

- Installation
- Configuration validation
- Site management
- Start, stop, reload, and restart

**Mail Trap Manager**

- Mailpit installation
- Process lifecycle
- SMTP configuration
- Mailpit API integration

**Configuration Manager**

- Application settings
- User configuration directories
- Runtime metadata
- Configuration generation
- Backups and rollback

**Update Manager**

- Release discovery
- Downloads
- Integrity validation
- Installation
- Rollback

# 5. Directory Structure

The application must separate installed binaries from user-owned data.

Default layout:

```text
%LOCALAPPDATA%/PhpDevTray/
|
+-- bin/
|   +-- php.cmd
|
+-- runtimes/
|   +-- php/
|   |   +-- 8.2.28/
|   |   +-- 8.3.27/
|   |   +-- 8.4.18/
|   |   +-- 8.5.3/
|   |
|   +-- nginx/
|   |   +-- 1.28.2/
|   |
|   +-- mailpit/
|       +-- version/
|
+-- user/
|   +-- php/
|   |   +-- 8.2/
|   |   |   +-- conf.d/
|   |   |       +-- 10-extensions.ini
|   |   |       +-- 20-custom.ini
|   |   |       +-- 90-managed.ini
|   |   +-- 8.3/
|   |   +-- 8.4/
|   |   +-- 8.5/
|   |
|   +-- nginx/
|       +-- nginx.conf
|       +-- sites-available/
|       +-- sites-enabled/
|       +-- generated/
|
+-- data/
|   +-- mailpit/
|   +-- state/
|
+-- logs/
|   +-- application/
|   +-- nginx/
|   +-- php/
|   +-- mailpit/
|
+-- downloads/
+-- backups/
+-- settings.json
```

The version numbers above are illustrative.

The user must be able to customize the application data directory during initial setup or through settings, subject to validation.

Runtime binaries are considered replaceable.

User configuration files must never be silently deleted or overwritten by runtime updates.

The stable `bin` directory contains the CLI launcher for the selected default PHP version.

# 6. PHP Runtime Management

## 6.1 Supported PHP versions

The application supports multiple installed PHP versions simultaneously.

Each installation is identified by its full version number.

Example:

- PHP 8.2.x
- PHP 8.3.x
- PHP 8.4.x
- PHP 8.5.x

Available versions are discovered from official PHP for Windows distribution metadata or a maintained, verified release manifest.

Unsupported or end-of-life versions may be displayed with a warning.

The application must not assume that all PHP releases remain downloadable indefinitely.

## 6.2 PHP installation

Installation workflow:

1. Retrieve available versions.
2. Allow the user to select a version.
3. Select a compatible Windows architecture and build.
4. Download the official distribution.
5. Verify integrity using trusted published checksums where available.
6. Extract into a temporary directory.
7. Validate the PHP executable.
8. Move the installation into the runtime directory.
9. Register installation metadata.
10. Create initial user configuration directories.
11. Allocate a FastCGI endpoint.
12. Make the runtime available in the UI.

Partial installations must be cleaned up after failure.

When the first PHP version is installed, the application must designate it as the default PHP version unless the user selects another installed version.

## 6.3 Runtime identity

A PHP runtime has:

- Unique runtime identifier.
- Full installed version.
- Minor-version channel.
- Executable path.
- Architecture.
- Thread-safety build type.
- FastCGI endpoint.
- Configuration directory.
- Installed extension inventory.
- Current process state.
- Associated Nginx sites.

The application should use supported Windows Non Thread Safe builds for FastCGI by default.

## 6.4 Simultaneous execution

Multiple PHP runtimes may run concurrently.

Each runtime must use a separate FastCGI endpoint.

Example:

| Runtime | Endpoint |
| --- | --- |
| PHP 8.2 | 127.0.0.1:9082 |
| PHP 8.3 | 127.0.0.1:9083 |
| PHP 8.4 | 127.0.0.1:9084 |
| PHP 8.5 | 127.0.0.1:9085 |

These are preferred defaults, not guaranteed assignments.

Ports must be checked for conflicts and persisted.

The application must support automatic reassignment when necessary.

## 6.5 Runtime controls

Each installed runtime provides:

- Start
- Stop
- Restart
- Set as Default PHP
- Edit user configuration
- Manage extensions
- Open runtime directory
- Open configuration directory
- View logs
- Check for updates
- Update
- Uninstall

Start and stop operations must be asynchronous and must not block the UI thread.

## 6.6 Runtime state machine

Each runtime has one of these states:

- Not Installed
- Installed
- Stopped
- Starting
- Running
- Stopping
- Restarting
- Updating
- Failed

Invalid state transitions must be rejected.

A runtime must not have two overlapping startup or shutdown operations.

## 6.7 Startup verification

Launching `php-cgi.exe` is not sufficient to consider a runtime healthy.

The supervisor must:

1. Launch the process.
2. Confirm the process remains alive.
3. Verify the expected FastCGI endpoint is available.
4. Perform a protocol-aware health check where practical.
5. Mark the runtime as Running only after verification succeeds.

A TCP connection alone is not sufficient to guarantee that PHP can process FastCGI requests.

Startup operations must have configurable timeouts.

## 6.8 Automatic runtime startup

The default behavior is to start PHP runtimes required by enabled Nginx sites.

A site referencing a stopped runtime can trigger its startup.

Manual start and stop actions must also be supported.

If a runtime required by an enabled site is manually stopped, the UI must warn that the site will become unavailable.

The application must not immediately undo an explicit manual stop.

Automatic startup should resume when the user requests it or when the application is restarted according to saved startup policy.

## 6.9 Default PHP version and PATH integration

The application must maintain exactly one selected default PHP version when at least one PHP runtime is installed.

The default PHP version is used for command-line execution of `php`, including Composer and other development tools that invoke PHP through PATH.

The default PHP version is independent of per-site PHP version assignments.

**PATH integration**

The application must create a stable CLI launcher directory:

```text
%LOCALAPPDATA%/PhpDevTray/bin/
```

The directory must be added to the current user's PATH, without requiring administrator privileges.

The application must:

1. Add the directory only once.
2. Avoid adding version-specific PHP directories to PATH.
3. Preserve unrelated PATH entries.
4. Detect conflicting PHP installations earlier in PATH.
5. Warn when the managed PHP launcher is not the command resolved by `php`.
6. Remove only its own PATH entry when the user disables integration or uninstalls the application.
7. Notify the user that existing terminal sessions may need to be reopened.

The launcher must resolve the currently selected default PHP version at execution time.

For Windows compatibility, the application may provide `php.cmd` or a dedicated lightweight `php.exe` launcher.

A dedicated executable launcher is preferred if necessary to support tools that require an executable rather than a command script.

**CLI configuration**

The default PHP launcher must apply the selected runtime's configuration, including its primary INI and scanned configuration fragments.

CLI execution must not require the PHP FastCGI service to be running.

**Changing the default version**

When the user selects another default PHP version:

1. Verify the version is installed.
2. Update the persisted default version.
3. Update the launcher target.
4. Verify that `php -v` resolves to the selected version through the managed launcher.
5. Preserve all Nginx site assignments.
6. Leave currently running FastCGI processes unaffected.

The application must prevent uninstalling the selected default PHP version without selecting a replacement or explicitly disabling PATH integration.

**Application lifecycle**

The stable launcher may remain installed in PATH when PHP DevTray is closed.

It launches PHP CLI commands on demand but must not start Nginx, PHP FastCGI, or Mailpit services.

# 7. PHP Configuration Management

## 7.1 Configuration principle

PHP installations must retain their stock configuration.

User settings must be stored separately.

PHP supports a primary configuration file followed by additional scanned INI files.

The application will use this mechanism.

## 7.2 Primary configuration

The PHP process must load the runtime's selected primary configuration.

The application may explicitly specify it using the PHP `-c` argument.

For a runtime with an appropriate stock configuration file:

```text
php-cgi.exe -c <runtime-config> -b 127.0.0.1:9084
```

The launcher must ensure the correct working directory and environment variables.

## 7.3 Additional configuration

The application sets `PHP_INI_SCAN_DIR` for each PHP process.

Example:

```text
PHP_INI_SCAN_DIR=<user-php-8.4-conf.d>
```

Configuration fragments are processed in filename order.

Recommended files:

**10-extensions.ini**

Application-managed extension loading directives.

**20-custom.ini**

User-owned general configuration.

**90-managed.ini**

Application-managed runtime customizations, including Mailpit SMTP configuration.

Example:

```ini
; Managed by PHP DevTray.
; Changes may be overwritten by the application.

[mail function]
SMTP = 127.0.0.1
smtp_port = 49327
```

The application must generate `90-managed.ini` independently for each PHP version.

Users must place personal configuration changes in `20-custom.ini`.

The application must not overwrite `20-custom.ini` during configuration changes or runtime updates.

## 7.4 Important PHP configuration behavior

Later INI values generally override earlier scalar settings, subject to directive-specific semantics.

Extension loading directives are additive.

The application must not assume that adding a later INI file can disable an extension already loaded by an earlier file.

To maintain reliable extension management, the application should control extension loading directives in its managed configuration and detect conflicting directives elsewhere.

The user must be informed when an extension cannot be disabled safely without modifying a conflicting configuration source.

Application-managed scalar settings in `90-managed.ini` intentionally take precedence over settings in `20-custom.ini`.

The application must clearly identify these managed settings in the UI.

## 7.5 Configuration validation

Before applying changes, the application must:

1. Create a backup when modifying managed configuration.
2. Validate the resulting PHP configuration.
3. Detect startup warnings.
4. Detect missing extension DLLs.
5. Detect duplicate extension loading.
6. Confirm the expected scanned configuration files.
7. Apply the change only if validation succeeds.

The application may use PHP command-line information and module-listing commands for validation.

For changes affecting the running process, restart the selected runtime.

Other PHP runtimes must remain unaffected.

# 8. PHP Extension Management

## 8.1 Extension discovery

For each installed PHP version, the application must identify:

- Built-in extensions.
- Dynamically loadable extensions.
- Enabled extensions.
- Disabled extensions.
- Missing extensions.
- Extensions that fail to load.

The extension inventory is version-specific.

## 8.2 Extension UI

The extension manager provides:

- Search field.
- Extension name.
- Enabled state.
- Extension type.
- Availability.
- Error or warning indicator.

Built-in extensions must be displayed as non-toggleable.

Dynamic extensions may be enabled or disabled.

## 8.3 Extension activation

When enabling an extension:

1. Verify the DLL exists.
2. Verify compatibility where metadata is available.
3. Check for duplicate configuration.
4. Update the managed extension INI file.
5. Validate configuration.
6. Restart the affected PHP runtime if running.
7. Verify the extension is loaded.
8. Update UI state.

## 8.4 Extension deactivation

When disabling an extension:

1. Remove or disable its managed loading directive.
2. Detect conflicting directives in other configuration files.
3. Validate configuration.
4. Restart the affected runtime.
5. Confirm the extension is no longer dynamically loaded.

## 8.5 Additional extension installation

Installing a new extension is distinct from enabling an existing one.

The application must verify:

- PHP API compatibility.
- Architecture.
- Thread-safety mode.
- Required dependencies.
- Trusted distribution source.

Version 1.0 may limit extension management to extensions bundled with the official PHP distribution.

Third-party extension installation is an optional later capability.

# 9. Nginx Management

## 9.1 Installation

The application supports installing and updating official Nginx Windows binary distributions obtained from [nginx.org](https://nginx.org/en/download.html).

Each Nginx version is installed in a dedicated runtime directory. User-owned configurations are stored separately and preserved during updates.

Installation workflow:

1. Discover available official Nginx releases.
2. Download the selected Windows ZIP distribution from the official source.
3. Verify archive integrity using trusted published checksums or signatures where available.
4. Extract into a version-specific runtime directory.
5. Validate the presence and executability of `nginx.exe`.
6. Register installation metadata.
7. Generate the initial user-owned Nginx configuration without modifying bundled runtime files.
8. Make Nginx available in the tray popup.

Partial installations must be cleaned up after failure.

The application must not treat individual websites or virtual hosts as installable Nginx distributions.

## 9.2 Runtime controls

Nginx provides:

- Start
- Stop
- Restart
- Reload
- Test configuration
- Edit user configuration
- Open sites directory
- View access log
- View error log
- Check for updates
- Update
- Uninstall

## 9.3 User-owned configuration

The user maintains a persistent Nginx configuration outside the runtime directory.

The application must use an explicit configuration path when launching `nginx.exe`.

The runtime's bundled configuration must not be modified.

The application should use a controlled working prefix and explicit configuration path so relative paths resolve consistently.

## 9.4 Configuration validation

Before starting or reloading Nginx, execute the equivalent of:

```text
nginx.exe -t -c <config-path> -p <prefix>
```

The command must use the same configuration and prefix as the running instance.

If validation fails:

- Do not reload.
- Preserve the running configuration.
- Display the error.
- Provide a link to the relevant configuration file.
- Preserve a recoverable copy of the last known good generated configuration.

# 10. Site Management

## 10.1 Site definition

Each Nginx site has:

- Unique identifier.
- Display name.
- Server name.
- Document root.
- Enabled state.
- Selected PHP version.
- User configuration path.
- Generated FastCGI configuration path.
- Optional additional aliases.

## 10.2 Site creation

The user can create a site from a template.

Required information:

- Site hostname.
- Document root.
- PHP version.

Optional information:

- Additional server names.
- Listening port.
- Custom Nginx directives.

## 10.3 Site configuration ownership

The application must support user-authored Nginx site configuration files.

The user must be able to edit these files using an external editor.

Application-managed configuration must be kept in clearly identified generated files.

The application must not silently rewrite arbitrary user-owned directives.

## 10.4 Site activation

The application must support enabling and disabling sites.

Because Nginx on Windows does not use filesystem symlink conventions as a built-in site management feature, the application should maintain an explicit enabled-site include list or generated configuration.

The application must validate the complete Nginx configuration before activating changes.

## 10.5 Per-site PHP version selection

Every PHP-enabled Nginx site must reference one installed PHP runtime channel.

Example:

```json
{
  "id": "api",
  "hostname": "api.test",
  "documentRoot": "C:/Projects/api/public",
  "phpVersion": "8.4",
  "enabled": true
}
```

The application resolves the selected PHP version to the active runtime endpoint.

Example:

```nginx
location ~ \.php$ {
    include fastcgi_params;

    fastcgi_param SCRIPT_FILENAME
        $document_root$fastcgi_script_name;

    fastcgi_pass 127.0.0.1:9084;
}
```

## 10.6 Switching PHP versions

When a user changes a site's PHP version:

1. Verify the target version is installed.
2. Ensure the target runtime is running.
3. Generate the new FastCGI routing configuration.
4. Validate Nginx.
5. Atomically replace managed configuration files where possible.
6. Reload Nginx.
7. Verify the site uses the expected PHP runtime.
8. Update persisted site metadata.

If validation or activation fails, restore the previous configuration and selection.

## 10.7 Local hostname resolution

Custom development hostnames may require Windows hosts-file entries or local DNS configuration.

The application must not modify the system hosts file silently.

If elevated privileges are required, the application should request permission only for that specific action.

Version 1.0 may provide instructions and an explicit hosts-file management command.

# 11. Mail Trap Integration

## 11.1 Technology

The application uses Mailpit as the local SMTP capture engine.

Mailpit runs as a managed child process.

It must not be installed as a permanent Windows Service.

## 11.2 Default endpoints

SMTP listener:

```text
127.0.0.1:49327
```

Mailpit HTTP interface:

```text
127.0.0.1:8025
```

The SMTP port must use an uncommon, application-specific default rather than standard SMTP development ports such as 25, 587, or 1025.

Both ports are configurable.

The application must detect port conflicts before startup.

If the SMTP port is occupied, the application must not silently connect PHP to the process already listening on that port.

Instead, it must require a free port, update the managed PHP configuration, and verify that Mailpit is listening on the selected endpoint.

The application must persist the chosen port across restarts.

## 11.3 Lifecycle

Mailpit provides:

- Start
- Stop
- Restart
- Open Inbox
- Clear Messages
- View Logs
- Update
- Configure

Mailpit must terminate when PHP DevTray exits.

## 11.4 PHP mail() integration

For Windows PHP runtimes, the application configures PHP's built-in SMTP settings through `90-managed.ini`.

Example:

```ini
; Managed by PHP DevTray.

[mail function]
SMTP = 127.0.0.1
smtp_port = 49327
```

Each PHP version can independently enable or disable Mail Trap integration.

The application must not modify the stock PHP configuration to enable Mailpit.

The application must regenerate the relevant managed INI files when the Mailpit SMTP port changes.

When Mailpit integration is disabled for a PHP version, the application must remove the managed SMTP directives for that version without altering the user's `20-custom.ini`.

If Mailpit is stopped, attempts to use the configured local SMTP endpoint should fail rather than be redirected to another SMTP service.

The application must verify endpoint ownership at Mailpit startup and must never intentionally route captured development email to an unrelated listener.

**Security limitation:** An uncommon port reduces the likelihood of accidental collisions but does not reserve the port. Another process could bind to it while Mailpit is stopped. The application must not claim that port selection alone guarantees email cannot reach another process.

## 11.5 Framework SMTP integration

The application must provide copyable connection details:

```text
Host: 127.0.0.1
Port: 49327
Encryption: None
Authentication: None
```

These defaults apply to the local-only development SMTP listener.

Users may configure frameworks such as Symfony Mailer, Laravel, and PHPMailer to use this endpoint.

If the configured Mailpit port changes, the displayed connection details must update immediately.

## 11.6 Mail inbox

The popup provides an integrated inbox powered by the Mailpit API.

Features:

- List captured messages.
- Search messages.
- View sender and recipients.
- View subject.
- View timestamp.
- View plain-text content.
- View HTML content.
- Inspect message headers.
- Inspect attachments.
- Save attachments.
- Delete messages.
- Clear the inbox.
- Display message count.

The application should use Mailpit's existing storage and API rather than introducing a second email database.

## 11.7 HTML preview

HTML messages may be rendered using WebView2.

Security requirements:

- Disable active scripting where feasible.
- Block remote resource loading by default.
- Prevent automatic navigation to external sites.
- Do not execute message-supplied scripts.
- Treat email content as untrusted.
- Require explicit user action before opening external links.

## 11.8 Message persistence

Captured messages must persist across application restarts unless the user clears them.

Storage location:

```text
data/mailpit/
```

The application should expose Mailpit-supported storage and retention controls where available.

## 11.9 SMTP security

Mailpit must:

- Bind to loopback by default.
- Use the configured uncommon SMTP port.
- Never relay messages to real recipients as part of normal operation.
- Avoid exposing its HTTP API publicly.
- Enforce configured message-size limits.
- Use a local persistent storage directory.
- Keep captured messages separate from application logs.

The application must not automatically enable Mailpit features that forward captured messages externally.

# 12. Process Supervision

## 12.1 Ownership

PHP DevTray owns all PHP FastCGI, Nginx, and Mailpit processes it starts.

Processes must not outlive the application.

PHP CLI commands launched independently through the default PATH integration are not managed background services and are not subject to this lifecycle requirement.

## 12.2 Windows Job Objects

The application should use Windows Job Objects with kill-on-close semantics for managed processes.

The supervisor must account for:

- PHP FastCGI process behavior.
- Nginx master and worker processes.
- Mailpit child process behavior.
- Graceful shutdown.
- Forced termination after timeout.

Process containment must be established before a managed process is allowed to run normally, avoiding races during process creation.

## 12.3 Shutdown sequence

On normal application exit:

1. Reject new management operations.
2. Stop accepting new UI commands.
3. Request graceful Nginx shutdown.
4. Stop PHP FastCGI runtimes.
5. Stop Mailpit.
6. Wait for processes to terminate.
7. Force termination of remaining owned processes after timeout.
8. Dispose Job Objects.
9. Dispose tray resources.
10. Exit the application.

If a graceful shutdown fails, the application must still guarantee cleanup of owned processes where Windows permits it.

## 12.4 Unexpected termination

If the application crashes, closes unexpectedly, or is terminated, its managed process containment must ensure cleanup.

The application must not terminate unrelated PHP or Nginx processes started by other software.

Process identity must be tracked using owned handles and runtime metadata, not executable names alone.

## 12.5 Health monitoring

The supervisor periodically checks:

- Process state.
- Expected listening endpoints.
- Service-specific readiness.
- Unexpected process exits.
- Startup timeout conditions.

Monitoring must not block the UI.

Unexpected failures should update the tray icon and service status.

Automatic restart policies must be configurable and bounded to avoid restart loops.

# 13. Updates

## 13.1 Supported updates

The application manages updates for:

- PHP runtimes.
- Nginx.
- Mailpit.
- PHP DevTray itself.

## 13.2 Update discovery

Updates must be discovered from trusted release sources.

The application must distinguish:

- Installed version.
- Latest available compatible version.
- Security or maintenance status where trustworthy metadata is available.
- Available update action.

## 13.3 Update procedure

Runtime updates should use staged installation:

1. Download release.
2. Verify integrity.
3. Extract to a new version directory.
4. Validate executable.
5. Preserve user configuration.
6. Stop or transition affected runtime.
7. Activate the new version.
8. Verify service health.
9. Roll back on failure.

Updates must not overwrite user configuration.

## 13.4 PHP channel updates

Nginx sites reference a PHP minor-version channel such as `8.4`.

Updating the active patch release within that channel should not require changing site assignments.

The default PHP CLI selection must also remain associated with its selected channel after a patch update.

Older patch releases may be retained temporarily for rollback.

## 13.5 Update notifications

The tray popup displays available updates.

Update checks may run periodically while the application is open.

Downloads and installations require explicit user approval unless an automatic-update policy has been enabled.

# 14. Application Settings

Settings must include:

**General**

- Start application with Windows.
- Start services when application launches.
- Theme selection.
- Notification preferences.
- Application data directory.

**PHP**

- Default PHP CLI version.
- User PATH integration.
- Default runtime startup behavior.
- FastCGI port allocation.
- Configuration directory.
- Extension management behavior.

**Nginx**

- Preferred HTTP port.
- Configuration path.
- Site directory.
- Reload behavior.

**Mail Trap**

- SMTP port, defaulting to 49327.
- HTTP interface port.
- Data directory.
- Retention settings.
- Per-version PHP mail integration.

**Updates**

- Automatic update checks.
- Check frequency.
- Release channel.
- Download behavior.

**Advanced**

- Logging verbosity.
- Startup timeout.
- Shutdown timeout.
- Health-check interval.
- Automatic restart policy.

# 15. Persistence and Data Models

## 15.1 Application settings

Store global settings in JSON.

Writes must be atomic where practical.

The settings must include the selected default PHP CLI channel and whether PATH integration is enabled.

Example:

```json
{
  "php": {
    "defaultChannel": "8.4",
    "pathIntegrationEnabled": true
  },
  "mailpit": {
    "smtpHost": "127.0.0.1",
    "smtpPort": 49327,
    "httpPort": 8025
  }
}
```

## 15.2 Runtime metadata

Each PHP installation stores:

```json
{
  "id": "php-8.4.18",
  "version": "8.4.18",
  "channel": "8.4",
  "architecture": "x64",
  "threadSafety": "NTS",
  "port": 9084,
  "autoStart": true
}
```

Runtime metadata must not be used as the sole source of truth for process health.

## 15.3 Site metadata

Example:

```json
{
  "id": "app",
  "hostname": "app.test",
  "documentRoot": "C:/Projects/app/public",
  "phpChannel": "8.5",
  "enabled": true,
  "managedFastCgi": true
}
```

## 15.4 State persistence

The application persists:

- Installed runtimes.
- Preferred runtime channels.
- Default PHP CLI version.
- PATH integration setting.
- Site assignments.
- Mailpit SMTP and HTTP ports.
- Per-version Mailpit integration.
- User preferences.
- Startup policies.
- Last selected UI section.

Transient process IDs and runtime health must be rediscovered rather than blindly restored.

# 16. Error Handling

All long-running operations must report:

- Current phase.
- Progress when measurable.
- Success or failure.
- Relevant error details.
- Recovery action.

Examples:

**Port conflict**

`PHP 8.4 cannot start because port 9084 is already in use.`

Available actions:

- Select another port.
- Retry.
- View details.

**Invalid Nginx configuration**

`Nginx configuration validation failed. The running configuration was not changed.`

Available actions:

- View error.
- Edit configuration.
- Retry.

**PHP extension failure**

`The gd extension could not be loaded by PHP 8.4.`

Available actions:

- View details.
- Restore previous configuration.

**Mailpit startup failure**

`Mail Trap could not start because SMTP port 49327 is unavailable.`

Available actions:

- Change port.
- Retry.

**PHP PATH conflict**

`Another PHP installation is taking precedence over the selected default PHP version.`

Available actions:

- View PATH details.
- Resolve conflict.
- Disable PATH integration.

Errors must be logged with enough detail for troubleshooting.

# 17. Security Requirements

The application must follow these principles:

1. Bind development services to loopback by default.
2. Never expose SMTP capture externally without explicit configuration.
3. Avoid unnecessary administrator privileges.
4. Verify downloaded runtime artifacts.
5. Treat user-edited configuration as untrusted input.
6. Avoid shell command construction from unescaped user strings.
7. Use structured process arguments.
8. Prevent directory traversal during archive extraction.
9. Avoid executing arbitrary downloaded scripts.
10. Protect configuration and mail data using appropriate user-level filesystem permissions.
11. Prevent WebView2 email previews from executing untrusted content.
12. Never delete user-owned configuration without confirmation.
13. Never terminate processes not owned by the application.
14. Provide explicit warnings before exposing development listeners to the network.
15. Do not silently overwrite unrelated user PATH entries.
16. Do not assume that an uncommon SMTP port guarantees exclusive ownership.
17. Never intentionally direct PHP mail to an SMTP endpoint occupied by an unrelated process.

# 18. Performance Requirements

Target characteristics:

- Tray-only operation with no visible main window.
- Responsive popup opening.
- Asynchronous service operations.
- Low idle CPU consumption.
- No continuous high-frequency polling.
- No unnecessary background browser process when the mail inbox is closed.
- Lazy loading of extension inventories and message content.
- Bounded logging and message storage.
- Minimal overhead for the default PHP CLI launcher.

Performance targets for acceptance testing:

| Metric | Target |
| --- | --- |
| Popup open latency | Under 200 ms on target hardware |
| Idle CPU usage | Near zero |
| Tray spinner refresh | Approximately 100 ms |
| UI response to commands | Under 100 ms before asynchronous work begins |
| Shutdown | Graceful completion within configured timeout |
| Memory | Measured and budgeted during prototype validation |

The final memory target must be established from a release-build prototype rather than assumed.

# 19. Accessibility

The interface should support:

- Keyboard navigation.
- Visible focus indicators.
- Screen-reader labels.
- High-DPI displays.
- Windows light and dark themes.
- Reduced-motion preferences.
- Non-color status indicators.
- Tooltips for icon-only controls.

The tray spinner should respect reduced-motion settings where feasible.

# 20. Testing Strategy

## 20.1 Unit tests

Test:

- Runtime state transitions.
- Port allocation.
- Version comparison.
- Default PHP version selection.
- PATH entry management.
- Site-to-PHP resolution.
- INI configuration management.
- Application-managed INI generation.
- Extension inventory.
- Settings serialization.
- Update planning.
- Error recovery decisions.

## 20.2 Integration tests

Test against actual PHP, Nginx, and Mailpit binaries.

Required scenarios:

- Start two PHP versions simultaneously.
- Route different Nginx sites to different PHP versions.
- Switch a site between PHP versions.
- Select a default PHP CLI version.
- Run `php -v` from a new terminal.
- Change the default PHP version without modifying site assignments.
- Run PHP CLI while the tray manager is closed.
- Enable and disable a PHP extension.
- Preserve user INI overrides.
- Generate SMTP settings in `90-managed.ini`.
- Change Mailpit SMTP port and regenerate managed configuration.
- Detect Mailpit SMTP port conflicts.
- Detect invalid Nginx configuration.
- Reload Nginx without interrupting unrelated PHP runtimes.
- Capture a PHP `mail()` message.
- Capture a framework SMTP message.
- Retrieve captured messages through Mailpit.
- Stop all managed processes on application exit.
- Recover from a failed runtime update.

## 20.3 Windows compatibility

Test on:

- Windows 10 x64.
- Windows 11 x64.
- Standard user accounts.
- High-DPI displays.
- Multi-monitor configurations.
- Paths containing spaces.
- Paths containing non-ASCII characters.
- Machines with existing PHP or Nginx installations.
- Machines with conflicting ports.
- Machines with existing PHP PATH entries.

## 20.4 Failure testing

Simulate:

- Failed downloads.
- Corrupted archives.
- Missing DLL dependencies.
- Invalid INI directives.
- Occupied ports.
- Unexpected PHP termination.
- Unexpected Nginx termination.
- Mailpit process failure.
- Another SMTP listener occupying the configured Mailpit port.
- Default PHP runtime removal.
- PATH conflicts.
- Application crash.
- Forced application termination.
- Interrupted updates.

# 21. Acceptance Criteria

Version 1.0 is considered complete when:

1. The application runs entirely from the Windows tray.
2. No traditional main window is required.
3. The Fluent popup opens and closes reliably.
4. The tray icon reflects service state.
5. Startup and shutdown operations display an animated spinner.
6. PHP versions can be installed independently.
7. Multiple PHP versions can run simultaneously.
8. A default PHP version can be selected.
9. The default PHP version is accessible through the user PATH.
10. Changing the default PHP version does not change Nginx site assignments.
11. PHP CLI works without the tray application running.
12. Each PHP version has its own configuration and extension state.
13. User INI overrides survive PHP updates.
14. PHP extensions can be enabled and disabled independently.
15. Application-managed settings are stored in `90-managed.ini`.
16. Nginx can be installed, started, stopped, and reloaded.
17. User-owned Nginx site configuration survives updates.
18. Individual sites can select their PHP version.
19. Switching PHP versions updates routing correctly.
20. Invalid configuration changes do not replace working configuration.
21. Mailpit can be installed and managed from the tray.
22. Mailpit uses a configurable uncommon SMTP port, defaulting to 49327.
23. PHP SMTP settings are generated in the application-managed INI.
24. Mailpit port conflicts are detected before startup.
25. PHP `mail()` messages are captured by Mailpit.
26. SMTP messages from development frameworks are captured.
27. Captured messages can be inspected through the popup.
28. All managed service processes terminate when the application exits.
29. Runtime updates preserve user configuration.
30. No permanent Windows Service is installed.
31. Normal operation does not require administrator privileges.
32. Failures are reported with actionable error information.
33. The application can be packaged as a self-contained Windows installer.

# 22. Development Roadmap

## Phase 1 — Application foundation

Deliverables:

- .NET solution.
- WPF tray host.
- Fluent popup.
- Three-command native context menu.
- Single-instance enforcement.
- Configuration storage.
- Logging infrastructure.
- Process supervisor.
- Animated tray icon.

## Phase 2 — PHP runtime management

Deliverables:

- PHP installation.
- Runtime discovery.
- Multi-version startup.
- Port allocation.
- Runtime health checks.
- Default PHP CLI selection.
- PATH integration.
- User INI configuration.
- Application-managed INI generation.
- Extension discovery and toggling.

## Phase 3 — Nginx and sites

Deliverables:

- Nginx installation.
- Configuration validation.
- Site discovery and creation.
- Site enable/disable.
- Per-site PHP version selection.
- Generated FastCGI configuration.
- Safe Nginx reload and rollback.

## Phase 4 — Mail Trap

Deliverables:

- Mailpit installation.
- Uncommon SMTP port configuration.
- SMTP port conflict detection.
- SMTP and HTTP endpoint management.
- Mailpit process supervision.
- PHP mail integration through `90-managed.ini`.
- Inbox interface.
- Message preview.
- Attachment handling.
- Mail retention controls.

## Phase 5 — Updates and hardening

Deliverables:

- Runtime update discovery.
- Staged updates.
- Rollback.
- Crash cleanup testing.
- Security validation.
- Installer.
- Automated integration tests.
- Release documentation.

# 23. Future Enhancements

Potential post-1.0 capabilities:

- Local HTTPS certificate management.
- Automatic `.test` hostname resolution.
- Composer integration.
- Xdebug management.
- Per-site PHP debugging profiles.
- Advanced support for externally managed PHP FastCGI configuration.
- Additional web servers.
- Database service management.
- Site templates for common frameworks.
- Import of existing Nginx configurations.
- Export and import of environment profiles.
- Project-specific environment variables.
- Backup and restore.
- Portable ZIP distribution.
- CLI companion for automation.

---

# 24. Final Architecture Decision

The initial implementation will use:

**Application:** C# / .NET 10  
**UI:** WPF with Fluent styling  
**Tray:** Native Windows notification icon  
**Context menu:** Three commands: Open Manager, Start All / Stop All, Exit Application  
**Process supervision:** Windows Job Objects  
**PHP:** Multiple concurrent Windows FastCGI runtimes  
**Default PHP:** Selectable CLI version through a stable user PATH launcher  
**PHP configuration:** Stock primary INI plus user scan-directory fragments  
**PHP extensions:** Per-version managed configuration  
**Managed PHP customization:** `90-managed.ini`, including Mailpit SMTP settings  
**Nginx:** Independently installed official Windows runtime  
**Sites:** User-owned Nginx configurations with managed FastCGI integration  
**Mail Trap:** Mailpit with local SMTP capture on configurable port 49327  
**Persistence:** JSON settings and Mailpit's own message storage  
**Lifecycle:** All managed services terminate with the tray application; independent PHP CLI commands remain available

The architecture should remain modular, with the runtime management layer independent from the presentation framework. This allows future replacement of WPF or introduction of a CLI without rewriting PHP, Nginx, or Mailpit management.

**End of specification — Version 1.1**
