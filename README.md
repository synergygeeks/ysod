# YsodGateModule: Subnet-Gated Error Detail for IIS

## What it does

Shows full exception detail (stack trace, error type, request info) in the
browser to internal users only. External users see the standard friendly
error page. All errors are logged to the Windows Event Log regardless.

## What changes

### 1. One DLL deployed to each web server

`YsodGateModule.dll` -- compiled from a single C# file, installed to the
GAC via `gacutil.exe /i YsodGateModule.dll`. Lands in:

```
C:\Windows\Microsoft.NET\assembly\GAC_MSIL\YsodGateModule\
```

### 2. Three lines added to root web.config

File location (same file where healthMonitoring defaults already live):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Config\web.config
```

Module registration (under `<system.webServer><modules>`):

```xml
<add name="YsodGateModule"
     type="Custom.IIS.YsodGateModule, YsodGateModule" />
```

Two appSettings entries:

```xml
<add key="TrustedProxies" value="192.0.2.10,192.0.2.11" />
<add key="InternalClientSubnets" value="10.0.0.0/8,172.16.0.0/12,192.168.0.0/16" />
```

`TrustedProxies` = Proxy IPs (exact IPs, not CIDR).
`InternalClientSubnets` = who sees full errors (CIDR ranges).

### 3. No per-app changes

The module runs server-wide. Individual apps are not modified.

## How it decides what to show

1. Is the request coming through a known Proxy? (checks immediate peer
   against TrustedProxies)
2. What is the real client IP? (reads X-Forwarded-For, walks it safely)
3. Is that client in an internal subnet? (checks against InternalClientSubnets)

All three must pass. If any fails, the user sees the friendly page.

## Rollback

Remove the `<add name="YsodGateModule" ... />` line from root web.config.
One line, immediate effect, no restart required beyond the automatic
app pool recycle.
