using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortableVoiceInput.Core;

public sealed record UpdateRelease(Version Version, string Tag, Uri DownloadUri, long Size, string Sha256);

/// <summary>Reads the published GitHub release; no credentials or user data are sent.</summary>
public sealed class GitHubUpdateClient(HttpClient http)
{
    public const string Repository = "amano0406/OneTalkText";
    public const string AssetName = "OneTalkText.exe";
    public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const long MaximumAssetBytes = 512L * 1024 * 1024;

    public async Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd("OneTalkText/" + Normalize(currentVersion).ToString(3));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid GitHub release metadata.");
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) return null;
        var tag = tagElement.GetString() ?? "";
        if (!Regex.IsMatch(tag, @"\Av?[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("The release tag is not a supported version.");
        version = Normalize(version);
        if (version <= Normalize(currentVersion)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The release has no downloadable assets.");
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != AssetName) continue;
            if (!asset.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded" ||
                !asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size <= 0 || size > MaximumAssetBytes ||
                !asset.TryGetProperty("digest", out var digestValue) || digestValue.ValueKind != JsonValueKind.String ||
                !asset.TryGetProperty("browser_download_url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The update asset is incomplete.");
            var digest = digestValue.GetString() ?? "";
            var expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{AssetName}";
            var url = urlValue.GetString() ?? "";
            if (!Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant) ||
                !string.Equals(url, expectedUrl, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update asset could not be verified.");
            return new UpdateRelease(version, tag, new Uri(expectedUrl), size, digest[7..].ToLowerInvariant());
        }
        throw new InvalidDataException("The release does not include the standalone executable.");
    }

    public async Task DownloadAsync(UpdateRelease release, string destination, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var created = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUri);
            request.Headers.UserAgent.ParseAdd("OneTalkText/" + release.Version.ToString(3));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072]; long total = 0; var lastPercent = -1;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > release.Size) throw new InvalidDataException("The downloaded update is larger than expected.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                digest.AppendData(buffer, 0, read);
                var percent = (int)(total * 100 / release.Size);
                if (percent != lastPercent) { lastPercent = percent; progress?.Report(percent); }
            }
            var expected = Convert.FromHexString(release.Sha256);
            if (total != release.Size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), expected))
                throw new InvalidDataException("The downloaded update failed its integrity check.");
            await output.FlushAsync(cancellationToken);
            output.Flush(true);
        }
        catch
        {
            if (created) { try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
    }

    public static void VerifyExecutableVersion(string executable, UpdateRelease release)
    {
        var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "";
        var versionText = productVersion.Split('+', 2)[0];
        if (!Version.TryParse(versionText, out var version) || Normalize(version) != Normalize(release.Version))
            throw new InvalidDataException("The downloaded executable version does not match the release.");
    }

    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
}
