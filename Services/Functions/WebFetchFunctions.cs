using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;

namespace Athena.UI.Services.Functions;

/// <summary>
/// Retrieval of a single public web resource into the filesystem sandbox.
/// <para>
/// This exists to close a structural gap rather than to add a feature: several tools need a file on
/// disk (presentation and document images, spreadsheet source data, attachments to parse) while every
/// other tool only ever returns text. Without a bounded way to land a URL, the only route is
/// <c>execute_terminal_command</c> — so fetching one JPEG grants arbitrary code execution for the
/// duration of the call. This tool serves the same need with an upper bound that cannot be argued
/// past: one GET, public addresses only, no execution, capped size, sandboxed destination.
/// </para>
/// </summary>
public sealed class WebFetchFunctions
{
    private const int MaxRedirects = 5;
    private const long AbsoluteMaxBytes = 64L * 1024 * 1024;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 300;

    /// <summary>
    /// Formats that execute rather than describe. The filesystem policy guards directories and size
    /// but not file types, so the refusal belongs here: landing a script and then running it through
    /// the terminal tool would reassemble exactly the capability this tool is meant to avoid.
    /// </summary>
    private static readonly HashSet<string> RefusedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".sys", ".msi", ".msp", ".com", ".scr", ".cpl",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".wsf", ".wsh", ".hta", ".lnk",
        ".sh", ".bash", ".zsh", ".fish", ".command", ".scpt", ".applescript",
        ".app", ".pkg", ".dmg", ".deb", ".rpm", ".apk", ".jar", ".class",
        ".py", ".pyc", ".pyo", ".rb", ".pl", ".php", ".js", ".mjs", ".cjs"
    };

    private static readonly HashSet<string> RefusedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-msdownload", "application/x-msdos-program", "application/x-executable",
        "application/vnd.microsoft.portable-executable", "application/x-sh", "application/x-shellscript",
        "application/x-dosexec", "application/x-mach-binary", "application/x-elf"
    };

    private readonly IFileSystemService _fileSystemService;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public WebFetchFunctions(IFileSystemService fileSystemService, HttpClient httpClient, ILogger logger)
    {
        _fileSystemService = fileSystemService;
        _httpClient = httpClient;
        _logger = logger.ForContext<WebFetchFunctions>();
    }

    public async Task<FunctionResult> FetchUrlToFileAsync(string url, string outputPath,
        bool overwrite = false, int timeoutSeconds = 60)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
                return FunctionResult.FailureResult($"Fetch failed: '{url}' is not an absolute URL.");

            var extension = Path.GetExtension(outputPath);
            if (RefusedExtensions.Contains(extension))
                return FunctionResult.FailureResult(
                    $"Fetch failed: '{extension}' is an executable or script format and cannot be downloaded. "
                    + "This tool retrieves documents, data and media only.");

            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath);
            if (File.Exists(fullOutputPath) && !overwrite)
                return FunctionResult.FailureResult(
                    "Fetch failed: Output file already exists. Set overwrite=true only when replacement is intended.");

            timeoutSeconds = Math.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ToolExecutionContext.CurrentCancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var (response, finalUri) = await SendFollowingRedirectsAsync(target, timeout.Token);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return FunctionResult.FailureResult(
                        $"Fetch failed: {finalUri} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}."
                        + (response.StatusCode == HttpStatusCode.TooManyRequests
                            ? " The host is rate limiting; wait before retrying and space out further requests."
                            : string.Empty));

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && RefusedMediaTypes.Contains(mediaType))
                    return FunctionResult.FailureResult($"Fetch failed: refusing executable content type '{mediaType}'.");

                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength > AbsoluteMaxBytes)
                    return FunctionResult.FailureResult(
                        $"Fetch failed: the resource declares {declaredLength} bytes, over the {AbsoluteMaxBytes} byte limit.");

                var written = await DownloadToFileAsync(response, fullOutputPath, overwrite, timeout.Token);

                _logger.Information("Fetched {Uri} to {Path} ({Bytes} bytes)", finalUri, fullOutputPath, written);
                return FunctionResult.SuccessResult("Resource fetched successfully.", new
                {
                    outputPath = fullOutputPath,
                    bytes = written,
                    contentType = mediaType,
                    finalUrl = finalUri.ToString(),
                    redirected = finalUri != target
                });
            }
        }
        catch (OperationCanceledException)
        {
            return FunctionResult.FailureResult(
                $"Fetch failed: the request did not complete within {timeoutSeconds}s, or was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return FunctionResult.FailureResult($"Fetch failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Fetch failed for {Url}", url);
            return FunctionResult.FailureResult($"Fetch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Follows redirects by hand so every hop is address-checked. Automatic redirect handling would
    /// validate only the URL the model supplied, leaving a public host free to bounce the request at
    /// a loopback or link-local address.
    /// </summary>
    private async Task<(HttpResponseMessage Response, Uri FinalUri)> SendFollowingRedirectsAsync(
        Uri target, CancellationToken cancellationToken)
    {
        var current = target;
        for (var hop = 0; ; hop++)
        {
            await GuardAddressAsync(current, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                return (response, current);

            var location = response.Headers.Location;
            response.Dispose();
            if (hop >= MaxRedirects)
                throw new HttpRequestException($"Exceeded {MaxRedirects} redirects starting from {target}.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Rejects anything that does not resolve to a public address.
    /// <para>
    /// A literal address is always checked — that is the direct route to loopback services and to
    /// cloud metadata at 169.254.169.254, and it is unambiguous. A hostname is only checked when the
    /// request is going out directly, because behind a proxy the local DNS answer is not the
    /// connection target: fake-ip proxies hand back reserved ranges (198.18/15, fc00::/7) for every
    /// name, which would both fail every fetch and prove nothing about where the proxy actually
    /// connects. In that setup the network boundary is the proxy's to enforce, and its own bypass
    /// list routes private destinations back through the direct path, where this check does apply.
    /// </para>
    /// <para>
    /// Residual gap either way: a host may answer differently between this lookup and the connection
    /// itself (DNS rebinding). Closing that means pinning the socket to a validated address, which is
    /// more machinery than this warrants — the ceiling here is still "one GET, no execution".
    /// </para>
    /// </summary>
    private static async Task GuardAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"Only http and https URLs can be fetched (got '{uri.Scheme}').");

        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            RejectIfNonPublic(uri, literal);
            return;
        }

        if (GoesThroughProxy(uri)) return;

        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0) throw new HttpRequestException($"Host '{uri.Host}' did not resolve.");
        foreach (var address in addresses) RejectIfNonPublic(uri, address);
    }

    private static void RejectIfNonPublic(Uri uri, IPAddress address)
    {
        if (!IsNonPublic(address)) return;
        throw new UnauthorizedAccessException(
            $"Refusing to fetch '{uri.Host}': it resolves to the non-public address {address}. "
            + "Only public internet resources can be retrieved.");
    }

    private static bool GoesThroughProxy(Uri uri)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            return proxy is not null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is not null;
        }
        catch
        {
            return false; // 拿不准就当直连，保留检查。
        }
    }

    private static bool IsNonPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6) return IsNonPublic(address.MapToIPv4());
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            if (address.Equals(IPAddress.IPv6Any)) return true;
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 or 127 => true,                                        // this-network, loopback
            10 => true,                                              // 10.0.0.0/8
            100 => octets[1] >= 64 && octets[1] <= 127,              // 100.64.0.0/10 CGNAT
            169 => octets[1] == 254,                                 // link-local, incl. cloud metadata
            172 => octets[1] >= 16 && octets[1] <= 31,               // 172.16.0.0/12
            192 => octets[1] == 168 || (octets[1] == 0 && octets[2] == 0), // 192.168/16, 192.0.0/24
            198 => octets[1] == 18 || octets[1] == 19,               // 198.18.0.0/15 benchmark, a common fake-ip range
            _ => octets[0] >= 224                                    // multicast and reserved
        };
    }

    /// <summary>
    /// Streams to a sibling temporary file, enforcing the cap as bytes arrive — a declared
    /// Content-Length is a claim, not a guarantee. The destination only appears once the whole body
    /// has landed and passed the sandbox's own size policy.
    /// </summary>
    private async Task<long> DownloadToFileAsync(HttpResponseMessage response, string fullOutputPath,
        bool overwrite, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.part");

        try
        {
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81_920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > AbsoluteMaxBytes)
                        throw new InvalidOperationException(
                            $"The response exceeded the {AbsoluteMaxBytes} byte limit and was abandoned.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            // Re-validate with the real size: the filesystem policy's write quota is authoritative and
            // may be stricter than this tool's own ceiling.
            _fileSystemService.GetAbsoluteSecureWritePath(fullOutputPath, written);
            File.Move(temporaryPath, fullOutputPath, overwrite);
            return written;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
