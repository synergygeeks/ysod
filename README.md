# YsodGateModule

IIS HTTP Module that gates full ASP.NET exception detail to trusted internal
clients only. External clients see the app's configured friendly error page.
All errors are logged to the Windows Event Log regardless of what the browser
sees.

## Why this exists

IIS's built-in error display modes only distinguish localhost vs everyone else.
This module adds:

- **Subnet-based decisions** -- CIDR matching against a configurable allow list
- **X-Forwarded-For awareness** -- reads real client IP from behind HAProxy
- **XFF trust validation** -- only trusts the header when the immediate peer
  is a known proxy, preventing spoofing

See `docs/ysod-gate-summary.md` for a manager-facing summary.

## Repo layout

```
src/
  YsodGateModule.cs     -- module source, all logic in one file
poc/
  web.config            -- PoC site config (healthMonitoring + module registration)
  Default.aspx          -- exception-triggering test page (button-driven)
  Default.aspx.cs       -- code-behind for Default.aspx
  Throw.aspx            -- GET-triggered exception page for curl testing
  Error.aspx            -- friendly error page shown to external clients
docs/
  ysod-gate-summary.md  -- non-technical summary for stakeholders
build.ps1               -- compiles YsodGateModule.cs using csc.exe
```

## Config (appSettings)

```xml
<add key="TrustedProxies"        value="192.0.2.10,192.0.2.11" />
<add key="InternalClientSubnets" value="10.0.0.0/8,172.16.0.0/12,192.168.0.0/16" />
```

`TrustedProxies` -- exact IPv4 addresses of HAProxy nodes. Not CIDR.
`InternalClientSubnets` -- CIDR ranges whose users may see full error detail.

## Build

```powershell
# PoC / dev (no strong name, bin\ deployment)
.\build.ps1

# Production (strong name, GAC deployment)
.\build.ps1 -ForGac
```

Requires only .NET Framework 4.x (`csc.exe`). No Visual Studio or SDK needed
for bin\ builds. GAC builds require `sn.exe` from the Windows SDK.

## Deploy: PoC (bin\ folder)

1. Run `.\build.ps1`
2. `New-Item -ItemType Directory C:\inetpub\wwwroot\<site>\bin -Force`
3. `Copy-Item YsodGateModule.dll C:\inetpub\wwwroot\<site>\bin\`
4. Add to the site's `web.config` under `<system.webServer>`:
   ```xml
   <modules>
     <add name="YsodGateModule"
          type="Contoso.IIS.YsodGateModule, YsodGateModule" />
   </modules>
   ```
5. Add `TrustedProxies` and `InternalClientSubnets` to `<appSettings>`

## Deploy: production (GAC + root web.config)

1. Run `.\build.ps1 -ForGac` -- note the full assembly name it outputs
2. `gacutil.exe /i YsodGateModule.dll` on each web server
3. Back up root web.config:
   ```
   %windir%\Microsoft.NET\Framework64\v4.0.30319\Config\web.config
   ```
4. Add `TrustedProxies` and `InternalClientSubnets` to `<appSettings>`
5. Add to `<system.webServer><modules>`:
   ```xml
   <add name="YsodGateModule"
        type="Contoso.IIS.YsodGateModule, YsodGateModule, Version=1.0.0.0, Culture=neutral, PublicKeyToken=REPLACE_WITH_ACTUAL_TOKEN" />
   ```
   Replace `PublicKeyToken` with the value from the build output.

> **Warning:** Editing root web.config triggers an app pool recycle on every
> app on the server. Schedule accordingly in production.

## Validate

From a machine whose IP is in `TrustedProxies`:

```powershell
# Expect: full exception detail (internal client)
curl.exe -i -H "X-Forwarded-For: 10.0.6.42" http://<server>/Throw.aspx

# Expect: friendly error page (external client)
curl.exe -i -H "X-Forwarded-For: 203.0.113.99" http://<server>/Throw.aspx

# Expect: friendly error page (no XFF)
curl.exe -i http://<server>/Throw.aspx
```

Also verify from a second machine NOT in `TrustedProxies` with an internal
XFF -- should always return the friendly page (critical security test).

Check Event Viewer after each test: Windows Logs -> Application, source
`ASP.NET *`. All errors should appear regardless of which response was shown.

## Rollback

Remove the `<add name="YsodGateModule" ... />` line from the relevant
`web.config`. Immediate effect. DLL can remain in GAC or bin\ harmlessly.

Full removal from GAC: `gacutil.exe /u YsodGateModule`

## Per-app opt-out

```xml
<system.webServer>
  <modules>
    <remove name="YsodGateModule" />
  </modules>
</system.webServer>
```

## Security notes

- `TrustedProxies` and `InternalClientSubnets` are security-sensitive.
  Treat them like firewall rules: reviewed, version-controlled, change-managed.
- Never use `0.0.0.0/0` in `InternalClientSubnets`.
- Never include `127.0.0.1` in `TrustedProxies` on production servers.
- Exception detail can include stack traces, file paths, and framework
  versions. Unauthorized exposure is a security finding.
- IPv4 only. IPv6 clients always see the friendly page.
