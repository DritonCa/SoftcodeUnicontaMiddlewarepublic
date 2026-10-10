namespace SoftcodeUnicontaMiddleware.Services;

/// <summary>
/// The caller's IP address for rate limiting and bot checks.
///
/// Behind Cloudflare the TCP peer is Cloudflare (or a reverse proxy), so the real
/// client is in the CF-Connecting-IP header. The header is only trusted when
/// "Network:TrustCloudflareHeader" is enabled, i.e. when the origin only accepts
/// traffic from Cloudflare; otherwise anyone could forge it.
/// </summary>
public static class ClientIp
{
    public static bool TrustCloudflareHeader { get; set; }

    public static string? Of(HttpContext context)
    {
        if (TrustCloudflareHeader)
        {
            var header = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(header))
                return header.Trim();
        }
        return context.Connection.RemoteIpAddress?.ToString();
    }
}
