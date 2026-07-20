using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageTray;

public enum UpdatePhase
{
    Downloading,
    Verifying,
    Preparing
}

public sealed record UpdateProgress(UpdatePhase Phase, long BytesReceived = 0, long? TotalBytes = null);

public sealed record ReleaseAsset(string Name, Uri DownloadUri, string Digest, long Size);

public sealed record AvailableUpdate(AppVersion Version, string Tag, ReleaseAsset Asset);

public sealed record StagedUpdate(AppVersion Version, string ExecutablePath, string StagingDirectory);

public sealed class ReleaseUpdateService : IDisposable
{
    private const long MaximumAssetBytes = 500L * 1024 * 1024;
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/Ayoshy/Codex-Meter/releases/latest");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _updatesRoot;

    public ReleaseUpdateService(HttpClient? httpClient = null, string? updatesRoot = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("CodexMeter", AppVersion.Current.ToString()));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _updatesRoot = updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexMeter",
            "updates");
    }

    public async Task<AvailableUpdate?> CheckAsync(
        AppVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _httpClient
            .GetAsync(LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var release = await JsonSerializer
            .DeserializeAsync<GitHubRelease>(stream, cancellationToken: timeout.Token)
            .ConfigureAwait(false);
        if (release is null ||
            release.Draft ||
            release.Prerelease ||
            !AppVersion.TryParse(release.TagName, out var releaseVersion) ||
            releaseVersion.CompareTo(currentVersion) <= 0)
        {
            return null;
        }

        var asset = SelectAsset(release.Assets, RuntimeInformation.ProcessArchitecture);
        return asset is null
            ? null
            : new AvailableUpdate(releaseVersion, release.TagName!, asset);
    }

    public async Task<StagedUpdate> DownloadAsync(
        AvailableUpdate update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAsset(update.Asset);
        var stagingDirectory = Path.Combine(
            _updatesRoot,
            $"{update.Version}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(stagingDirectory, "update.zip.partial");
        var executablePath = Path.Combine(stagingDirectory, "CodexMeter.exe");

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            using var response = await _httpClient
                .GetAsync(update.Asset.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength ?? update.Asset.Size;
            if (contentLength is <= 0 or > MaximumAssetBytes)
            {
                throw new InvalidDataException("The update archive has an invalid size.");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long totalRead = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead += read;
                    if (totalRead > MaximumAssetBytes)
                    {
                        throw new InvalidDataException("The update archive exceeds the size limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalRead, contentLength));
                }
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Verifying));
            if (!await DigestMatchesAsync(archivePath, update.Asset.Digest, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The update archive failed SHA-256 verification.");
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Preparing));
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var executableEntry = archive.Entries.SingleOrDefault(entry =>
                    entry.FullName.Replace('\\', '/').Equals("CodexMeter.exe", StringComparison.OrdinalIgnoreCase));
                if (executableEntry is null ||
                    executableEntry.Length is <= 64 * 1024 or > MaximumAssetBytes)
                {
                    throw new InvalidDataException("The update archive does not contain a valid CodexMeter.exe.");
                }

                await using var entryStream = executableEntry.Open();
                await using var executableStream = new FileStream(
                    executablePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);
                await entryStream.CopyToAsync(executableStream, cancellationToken).ConfigureAwait(false);
            }

            await VerifyExecutableAsync(executablePath, update.Version, cancellationToken).ConfigureAwait(false);
            File.Delete(archivePath);
            return new StagedUpdate(update.Version, executablePath, stagingDirectory);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public static ReleaseAsset? SelectAsset(
        IEnumerable<GitHubReleaseAsset>? assets,
        Architecture architecture)
    {
        var runtime = architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null
        };
        if (runtime is null || assets is null)
        {
            return null;
        }

        var expectedName = $"CodexMeter-{runtime}-standalone.zip";
        var asset = assets.FirstOrDefault(item => string.Equals(
            item.Name,
            expectedName,
            StringComparison.OrdinalIgnoreCase));
        return asset is null ||
               string.IsNullOrWhiteSpace(asset.Digest) ||
               !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri)
            ? null
            : new ReleaseAsset(expectedName, downloadUri, asset.Digest, asset.Size);
    }

    public static async Task<bool> DigestMatchesAsync(
        string path,
        string expectedDigest,
        CancellationToken cancellationToken = default)
    {
        const string prefix = "sha256:";
        if (!expectedDigest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected = expectedDigest[prefix.Length..];
        if (expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateAsset(ReleaseAsset asset)
    {
        if (asset.Size is <= 0 or > MaximumAssetBytes ||
            !asset.DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(asset.DownloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              asset.DownloadUri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
              asset.DownloadUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)) ||
            !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release asset is not eligible for automatic updates.");
        }
    }

    private static async Task VerifyExecutableAsync(
        string executablePath,
        AppVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await using (var stream = File.OpenRead(executablePath))
        {
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != header.Length ||
                header[0] != (byte)'M' ||
                header[1] != (byte)'Z')
            {
                throw new InvalidDataException("The staged update is not a Windows executable.");
            }
        }

        var productVersion = System.Diagnostics.FileVersionInfo
            .GetVersionInfo(executablePath)
            .ProductVersion;
        if (!AppVersion.TryParse(productVersion, out var executableVersion) ||
            executableVersion != expectedVersion)
        {
            throw new InvalidDataException("The staged executable version does not match the release tag.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A future cleanup pass will remove stale update staging folders.
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset>? Assets);

    public sealed record GitHubReleaseAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("size")] long Size);
}
