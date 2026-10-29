# Nginx include files

Open a site's pencil action to edit its file under `user/nginx/sites-available/`. Missing site files are created on initialization/save, including disabled sites; existing files are preserved. The page's folder shortcut opens `user/nginx/`.

Direct `include` directives written in a site file must use absolute paths. Absolute wildcard masks are accepted too. For example:

```nginx
include "C:/Dev/nginx-snippets/common.conf";
include "C:/Dev/nginx-snippets/locations/*.conf";
```

Keep the site's generated `@lsm/php/<site-id>@` marker. The manager replaces that marker with the selected channel's PHP routing when it builds a candidate. Other direct includes remain unchanged. Use forward slashes and quote paths containing spaces, following [Nginx's Windows guidance](https://nginx.org/en/docs/windows.html).

Included files may themselves use normal Nginx include syntax: absolute/relative paths and wildcard masks. These files are passed to Nginx without recursive rewriting or extra manager restrictions. Nginx validates their syntax and directive contexts with `nginx -t`; its [include documentation](https://nginx.org/en/docs/ngx_core_module.html#include) describes files and masks.

Relative **include** paths resolve from the main configuration prefix, rather than the directory of the including snippet. In this app that directory is `<data-directory>/user/nginx/generated/`. Validation and startup both use main bootstrap files in that directory, so they resolve the same relative paths. This behavior was confirmed against actual Nginx; see also the [Nginx maintainer's explanation](https://mailman.nginx.org/pipermail/nginx/2016-April/050456.html).

After editing, stop/start Nginx with its summary-bar icon to validate and activate changes. There is no automatic reload on editor save. Site enable/channel changes also validate the complete candidate before activation. A rejected candidate leaves the previous bootstrap and working processes in place. A manually stopped service stays stopped if its next start fails.

The managed root `user/nginx/nginx.conf` still accepts its required include markers only. Site hostname/root/listener metadata checks and direct FastCGI restrictions remain. Nested contents are user-owned Nginx configuration: the manager does not recursively enforce those semantic restrictions, snapshot external snippets, or watch them for changes. Avoid editing included files during activation. Native Nginx syntax/platform limitations still apply; “nested support” does not make invalid directives valid.

See [REAL-NGINX-INCLUDES.md](REAL-NGINX-INCLUDES.md) for actual startup, HTTP routing, relative/glob resolution, source preservation and rejection checks.
