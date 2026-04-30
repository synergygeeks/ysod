using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Web;

namespace Custom.IIS
{
    /// <summary>
    /// IIS HTTP Module that gates full exception detail (YSOD-equivalent)
    /// to trusted internal clients only. External clients see whatever
    /// customErrors page the app has configured.
    ///
    /// Designed for server-wide deployment via the GAC + root web.config,
    /// so individual apps require zero changes.
    ///
    /// Trust model:
    ///   1. The immediate TCP peer (Request.UserHostAddress) must be in the
    ///      TrustedProxies appSetting (exact IPs, comma-separated).
    ///      If not, X-Forwarded-For is ignored entirely.
    ///   2. X-Forwarded-For is walked right-to-left, skipping trusted-proxy
    ///      hops, until a non-proxy IP is found. That is the real client.
    ///   3. The real client IP must be in one of the InternalClientSubnets
    ///      (CIDR notation, comma-separated) to see full exception detail.
    ///
    /// If any step fails, the request falls through to the app's normal
    /// customErrors handling.
    ///
    /// IPv4 only. IPv6 clients always see the friendly page.
    ///
    /// Config (appSettings in web.config or root web.config):
    ///   TrustedProxies         - comma-separated exact IPv4 addresses
    ///   InternalClientSubnets  - comma-separated CIDR ranges
    ///
    /// Depends on: System.Web, System.Configuration (both in the GAC)
    /// Called by: IIS module pipeline (registered in web.config modules section)
    /// </summary>
    public class YsodGateModule : IHttpModule
    {
        // --- CACHED CONFIG ---
        // Loaded once per app domain lifetime via Lazy<T>. Reset happens
        // automatically when web.config changes trigger an app domain recycle.

        private static readonly Lazy<HashSet<IPAddress>> CachedTrustedProxies =
            new Lazy<HashSet<IPAddress>>(LoadTrustedProxies);

        private static readonly Lazy<List<Cidr>> CachedInternalSubnets =
            new Lazy<List<Cidr>>(LoadInternalSubnets);

        // --- IHttpModule IMPLEMENTATION ---

        public void Init(HttpApplication context)
        {
            context.Error += OnError;
        }

        public void Dispose()
        {
            // No unmanaged resources to release.
        }

        // --- CORE LOGIC ---

        private void OnError(object sender, EventArgs e)
        {
            HttpApplication app = sender as HttpApplication;
            if (app == null) return;

            HttpContext ctx = app.Context;
            if (ctx == null) return;

            Exception ex = ctx.Server.GetLastError();
            if (ex == null) return;

            // Unwrap the HttpUnhandledException wrapper ASP.NET adds before
            // the error event fires. The InnerException is the real one.
            Exception real = (ex is HttpUnhandledException && ex.InnerException != null)
                ? ex.InnerException
                : ex;

            if (!ShouldShowFullErrors(ctx.Request))
            {
                // Not a trusted internal client. Leave the error in place
                // and let the app's customErrors handle the response.
                return;
            }

            // Trusted internal client: render full exception detail.
            // ClearError() is required so customErrors doesn't override our
            // response. healthMonitoring still logs the exception to the
            // Event Log regardless (verified during PoC validation).
            ctx.Server.ClearError();
            ctx.Response.Clear();
            ctx.Response.StatusCode = 500;
            ctx.Response.TrySkipIisCustomErrors = true;
            ctx.Response.ContentType = "text/html";

            ctx.Response.Write("<!DOCTYPE html><html><head>");
            ctx.Response.Write("<title>Server Error (Internal View)</title>");
            ctx.Response.Write("<style>");
            ctx.Response.Write("body { font-family: Consolas, monospace; margin: 2em; }");
            ctx.Response.Write("h1 { color: #a00; }");
            ctx.Response.Write("pre { background: #f4f4f4; padding: 1em; overflow: auto; }");
            ctx.Response.Write(".banner { background: #fff3cd; border: 1px solid #ffc107;");
            ctx.Response.Write("  padding: 0.75em 1em; margin-bottom: 1.5em; }");
            ctx.Response.Write("</style>");
            ctx.Response.Write("</head><body>");

            ctx.Response.Write("<div class='banner'>");
            ctx.Response.Write("<strong>Internal diagnostic view.</strong> ");
            ctx.Response.Write("This is only shown to trusted internal clients. ");
            ctx.Response.Write("External users see the standard error page.");
            ctx.Response.Write("</div>");

            ctx.Response.Write("<h1>Server Error</h1>");

            // Primary exception
            WriteExceptionBlock(ctx.Response, real, null);

            // Inner exception chain
            Exception inner = real.InnerException;
            int depth = 1;
            while (inner != null)
            {
                WriteExceptionBlock(ctx.Response, inner, depth);
                inner = inner.InnerException;
                depth++;
            }

            // Request context
            ctx.Response.Write("<h2>Request</h2>");
            ctx.Response.Write("<p><strong>URL:</strong> ");
            ctx.Response.Write(HttpUtility.HtmlEncode(ctx.Request.Url.ToString()));
            ctx.Response.Write("</p>");
            ctx.Response.Write("<p><strong>HTTP method:</strong> ");
            ctx.Response.Write(HttpUtility.HtmlEncode(ctx.Request.HttpMethod));
            ctx.Response.Write("</p>");
            ctx.Response.Write("<p><strong>Immediate peer:</strong> ");
            ctx.Response.Write(HttpUtility.HtmlEncode(ctx.Request.UserHostAddress ?? ""));
            ctx.Response.Write("</p>");
            ctx.Response.Write("<p><strong>X-Forwarded-For:</strong> ");
            ctx.Response.Write(HttpUtility.HtmlEncode(
                ctx.Request.Headers["X-Forwarded-For"] ?? "(none)"));
            ctx.Response.Write("</p>");
            ctx.Response.Write("<p><strong>User-Agent:</strong> ");
            ctx.Response.Write(HttpUtility.HtmlEncode(
                ctx.Request.UserAgent ?? "(none)"));
            ctx.Response.Write("</p>");
            ctx.Response.Write("<p><strong>Timestamp (UTC):</strong> ");
            ctx.Response.Write(HttpUtility.HtmlEncode(DateTime.UtcNow.ToString("o")));
            ctx.Response.Write("</p>");

            ctx.Response.Write("</body></html>");

            try
            {
                ctx.Response.End();
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Response.End() always throws ThreadAbortException in
                // classic ASP.NET. This is expected and safe to swallow.
            }
        }

        private static void WriteExceptionBlock(HttpResponse response, Exception ex, int? depth)
        {
            string heading = depth.HasValue
                ? "Inner Exception (depth " + depth.Value + ")"
                : "Exception";

            response.Write("<h2>" + heading + "</h2>");
            response.Write("<p><strong>Type:</strong> ");
            response.Write(HttpUtility.HtmlEncode(ex.GetType().FullName));
            response.Write("</p>");
            response.Write("<p><strong>Message:</strong> ");
            response.Write(HttpUtility.HtmlEncode(ex.Message));
            response.Write("</p>");
            response.Write("<pre>");
            response.Write(HttpUtility.HtmlEncode(ex.StackTrace ?? "(none)"));
            response.Write("</pre>");
        }

        // --- IP RESOLUTION ---

        /// <summary>
        /// Returns true if the current request should see full exception detail.
        /// </summary>
        private static bool ShouldShowFullErrors(HttpRequest request)
        {
            if (request == null) return false;

            // Step 1: is the immediate TCP peer a trusted proxy?
            IPAddress peer;
            if (!IPAddress.TryParse(request.UserHostAddress, out peer))
                return false;

            if (!CachedTrustedProxies.Value.Contains(peer))
                return false;

            // Step 2: walk X-Forwarded-For to find the real client.
            string xff = request.Headers["X-Forwarded-For"];
            IPAddress realClient = ResolveRealClient(xff);
            if (realClient == null)
                return false;

            // Step 3: is the real client in an allowed internal subnet?
            return CachedInternalSubnets.Value.Any(c => c.Contains(realClient));
        }

        /// <summary>
        /// Walks an X-Forwarded-For header right-to-left, skipping entries
        /// that are themselves trusted proxies, and returns the first
        /// non-proxy address. Returns null if XFF is missing, empty, or
        /// contains only trusted-proxy hops.
        /// </summary>
        private static IPAddress ResolveRealClient(string xffHeader)
        {
            if (string.IsNullOrWhiteSpace(xffHeader))
                return null;

            string[] entries = xffHeader.Split(',');
            IPAddress addr;
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                string entry = entries[i].Trim();
                if (!IPAddress.TryParse(entry, out addr))
                    continue;

                if (!CachedTrustedProxies.Value.Contains(addr))
                    return addr;
            }

            return null;
        }

        // --- CONFIG LOADING ---

        private static HashSet<IPAddress> LoadTrustedProxies()
        {
            var set = new HashSet<IPAddress>();
            string raw = ConfigurationManager.AppSettings["TrustedProxies"] ?? "";
            IPAddress addr;
            foreach (string entry in raw.Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0) continue;
                if (IPAddress.TryParse(trimmed, out addr))
                    set.Add(addr);
            }
            return set;
        }

        private static List<Cidr> LoadInternalSubnets()
        {
            var list = new List<Cidr>();
            string raw = ConfigurationManager.AppSettings["InternalClientSubnets"] ?? "";
            Cidr cidr;
            foreach (string entry in raw.Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0) continue;
                if (Cidr.TryParse(trimmed, out cidr))
                    list.Add(cidr);
            }
            return list;
        }

        // --- CIDR SUPPORT ---

        /// <summary>
        /// Minimal IPv4 CIDR representation. Stores network and mask as uint
        /// and tests membership by bitwise AND. No dependency on
        /// System.Net.IPNetwork (unavailable in this framework version).
        /// </summary>
        private sealed class Cidr
        {
            private readonly uint _network;
            private readonly uint _mask;

            private Cidr(uint network, uint mask)
            {
                _network = network;
                _mask = mask;
            }

            public bool Contains(IPAddress address)
            {
                // IPv4 only. IPv6 addresses return false rather than
                // throwing, since they may appear in mixed environments.
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    return false;

                uint addr = ToUInt32(address);
                return (addr & _mask) == _network;
            }

            public static bool TryParse(string cidr, out Cidr result)
            {
                result = null;
                if (string.IsNullOrWhiteSpace(cidr)) return false;

                string[] parts = cidr.Split('/');
                if (parts.Length != 2) return false;

                IPAddress addr;
                if (!IPAddress.TryParse(parts[0], out addr)) return false;
                if (addr.AddressFamily != AddressFamily.InterNetwork)
                    return false;

                int prefix;
                if (!int.TryParse(parts[1], out prefix)) return false;
                if (prefix < 0 || prefix > 32) return false;

                // Build mask: top N bits set, rest zero.
                // prefix 0 = match anything; shift by 32 is undefined, so
                // handle it explicitly.
                uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
                uint network = ToUInt32(addr) & mask;

                result = new Cidr(network, mask);
                return true;
            }

            private static uint ToUInt32(IPAddress address)
            {
                byte[] bytes = address.GetAddressBytes();
                // GetAddressBytes returns network byte order (big-endian).
                return ((uint)bytes[0] << 24)
                     | ((uint)bytes[1] << 16)
                     | ((uint)bytes[2] << 8)
                     |  (uint)bytes[3];
            }
        }
    }
}
