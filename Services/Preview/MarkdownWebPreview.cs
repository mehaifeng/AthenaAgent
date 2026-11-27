using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Athena.UI.Services.Functions;

namespace Athena.UI.Services.Preview;

internal sealed record WebPageSummary(string? Title, string? Description);

/// <summary>Reads a bounded HTML head for a hovered link. The browser session is independent.</summary>
internal sealed class MarkdownWebPreview(HttpClient client) : IDisposable
{
    private readonly ConditionalWeakTable<Uri, WebPageSummary> _cache = new();

    // The DI container disposes the preview, which owns the client and its message handler.
#pragma warning disable CA2000
    internal static MarkdownWebPreview Create() => new(new HttpClient(new SocketsHttpHandler
    {
        UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
        MaxResponseDrainSize = 0, MaxResponseHeadersLength = 32, ConnectCallback = ConnectAsync
    }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan });
#pragma warning restore CA2000

    internal static bool IsWebTarget(Uri url) =>
        url.IsAbsoluteUri && url.Scheme is "http" or "https" && url.UserInfo.Length == 0;

    private static bool CanFetch(Uri url)
    {
        if (!IsWebTarget(url) || url.IsLoopback) return false;
        var host = url.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out var ip)) return !WebFetchFunctions.IsNonPublic(ip);
        return host.Contains('.') && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var endpoint = context.DnsEndPoint;
        var request = context.InitialRequestMessage;
        Socket? socket = new(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            if (request.Method == HttpMethod.Connect || request.RequestUri is { } uri &&
                (!uri.IdnHost.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != endpoint.Port))
                await socket.ConnectAsync(endpoint, token).ConfigureAwait(false); // The configured system proxy.
            else
            {
                var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, token).ConfigureAwait(false);
                if (addresses.Length == 0 || addresses.Any(WebFetchFunctions.IsNonPublic))
                    throw new HttpRequestException("The preview target is not a public address.");
                await socket.ConnectAsync(addresses, endpoint.Port, token).ConfigureAwait(false);
            }
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        finally { socket?.Dispose(); }
    }

    internal async Task<WebPageSummary?> ReadAsync(Uri address, CancellationToken token)
    {
        if (_cache.TryGetValue(address, out var cached)) return cached;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var current = address;
        for (var hop = 0; hop <= 3 && CanFetch(current); hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 &&
                response.Headers.Location is { } location && Uri.TryCreate(current, location, out var next))
            {
                current = next;
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                is not ("text/html" or "application/xhtml+xml")) return null;
            using var body = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            var bytes = new byte[64 * 1024];
            var length = await body.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, budget.Token).ConfigureAwait(false);
            using var prefix = new MemoryStream(bytes, 0, length, writable: false);
            using var document = await new HtmlParser().ParseDocumentAsync(prefix, budget.Token).ConfigureAwait(false);
            string? Meta(string selector) => document.Head?.QuerySelector(selector)?.GetAttribute("content");
            var summary = new WebPageSummary(Caption(Meta("meta[property='og:title']") ?? document.Title, 180),
                Caption(Meta("meta[property='og:description']") ?? Meta("meta[name='description']"), 400));
            return _cache.GetValue(address, _ => summary);
        }
        return null;
    }

    private static string? Caption(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }

    public void Dispose() => client.Dispose();
}
