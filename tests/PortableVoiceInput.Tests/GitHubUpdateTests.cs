using System.Net;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using PortableVoiceInput.Core;

namespace PortableVoiceInput.Tests;

public sealed class GitHubUpdateTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic update payload");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    [Fact]
    public async Task NewerPublicReleaseIsSelectedWithoutCredentials()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(GitHubUpdateClient.LatestReleaseUrl, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Json(Release("v0.1.5"));
        }));
        var release = await new GitHubUpdateClient(http).CheckAsync(new Version(0, 1, 4, 0));
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 1, 5, 0), release.Version);
        Assert.Equal(Hash, release.Sha256);
        Assert.Equal(Payload.Length, release.Size);
    }

    [Fact]
    public async Task PublishedGitHubReleaseCanBeReadWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("ONETALK_LIVE_GITHUB_UPDATES") != "1") return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var release = await new GitHubUpdateClient(http).CheckAsync(new Version(0, 0, 0));
        Assert.NotNull(release);
        Assert.Equal(64, release.Sha256.Length);
        Assert.True(release.Size > 0);
    }

    [Fact]
    public void ExecutableVersionMustMatchTheReleaseWhenExplicitlyEnabled()
    {
        var executable = Environment.GetEnvironmentVariable("ONETALK_TEST_EXE");
        if (string.IsNullOrWhiteSpace(executable)) return;
        var release = new UpdateRelease(new Version(0, 1, 5), "v0.1.5", new Uri("https://github.com/amano0406/OneTalkText/releases/download/v0.1.5/OneTalkText.exe"), 1, Hash);
        GitHubUpdateClient.VerifyExecutableVersion(executable, release);
        Assert.Throws<InvalidDataException>(() => GitHubUpdateClient.VerifyExecutableVersion(executable, release with { Version = new Version(0, 1, 6) }));
    }

    [Theory]
    [InlineData("v0.1.4")]
    [InlineData("v0.1.3")]
    public async Task SameOrOlderReleaseIsNotApplied(string tag)
    {
        using var http = new HttpClient(new Handler(_ => Json(Release(tag))));
        Assert.Null(await new GitHubUpdateClient(http).CheckAsync(new Version(0, 1, 4, 0)));
    }

    [Fact]
    public async Task PrereleasesAndDraftsAreIgnored()
    {
        using var http = new HttpClient(new Handler(_ => Json(Release("v0.1.5", prerelease: true))));
        Assert.Null(await new GitHubUpdateClient(http).CheckAsync(new Version(0, 1, 4)));
    }

    [Theory]
    [InlineData("https://example.com/OneTalkText.exe", "valid")]
    [InlineData("http://github.com/amano0406/OneTalkText/releases/download/v0.1.5/OneTalkText.exe", "valid")]
    [InlineData("https://github.com/amano0406/OneTalkText/releases/download/v0.1.5/OneTalkText.exe", "invalid")]
    public async Task RejectsUntrustedUrlOrMissingIntegrityDigest(string url, string digestKind)
    {
        using var http = new HttpClient(new Handler(_ => Json(Release("v0.1.5", url: url, digest: digestKind == "valid" ? "sha256:" + Hash : ""))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubUpdateClient(http).CheckAsync(new Version(0, 1, 4)));
    }

    [Fact]
    public async Task DownloadVerifiesBytesAndReportsProgress()
    {
        using var workspace = new TestWorkspace();
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }));
        var release = new UpdateRelease(new Version(0, 1, 5), "v0.1.5", new Uri("https://github.com/amano0406/OneTalkText/releases/download/v0.1.5/OneTalkText.exe"), Payload.Length, Hash);
        var percentages = new List<int>();
        var path = workspace.File("download.tmp");
        await new GitHubUpdateClient(http).DownloadAsync(release, path, new InlineProgress(percentages.Add));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(100, percentages[^1]);
    }

    [Fact]
    public async Task CorruptDownloadIsRemovedWithoutTouchingRecordingOrSettings()
    {
        using var workspace = new TestWorkspace();
        var config = workspace.File("config.ini"); var recording = workspace.File("recording.wav");
        await File.WriteAllTextAsync(config, "private settings"); await File.WriteAllTextAsync(recording, "original audio");
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }));
        var release = new UpdateRelease(new Version(0, 1, 5), "v0.1.5", new Uri("https://github.com/amano0406/OneTalkText/releases/download/v0.1.5/OneTalkText.exe"), Payload.Length, new string('0', 64));
        var path = workspace.File("download.tmp");
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubUpdateClient(http).DownloadAsync(release, path));
        Assert.False(File.Exists(path));
        Assert.Equal("private settings", await File.ReadAllTextAsync(config));
        Assert.Equal("original audio", await File.ReadAllTextAsync(recording));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverRemovedOrOverwritten()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("existing.tmp"); await File.WriteAllTextAsync(path, "keep me");
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }));
        var release = new UpdateRelease(new Version(0, 1, 5), "v0.1.5", new Uri("https://github.com/amano0406/OneTalkText/releases/download/v0.1.5/OneTalkText.exe"), Payload.Length, Hash);
        await Assert.ThrowsAnyAsync<IOException>(() => new GitHubUpdateClient(http).DownloadAsync(release, path));
        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void InstanceMutexMatchesTheExistingPortableAppName()
    {
        using var workspace = new TestWorkspace();
        var folder = workspace.File("app") + Path.DirectorySeparatorChar;
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant())))[..16];
        Assert.Equal("Local\\PortableVoiceInput-" + expected, UpdatePaths.InstanceMutexName(folder));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HelperArgumentsCannotEscapeThePortableWorkingFolder()
    {
        using var workspace = new TestWorkspace();
        var id = Guid.NewGuid();
        var paths = new UpdatePaths(workspace.File("app"), "OneTalkText.exe", id);
        var args = new[] { UpdateHandoff.HelperSwitch, id.ToString("N"), "1234", Hash, "42", "OneTalkText.exe", "ja", "1" };
        Assert.True(UpdateHandoff.TryParseHelper(args, paths.HelperExecutable, out var invocation));
        Assert.NotNull(invocation);
        Assert.Equal(paths.TargetExecutable, invocation.Paths.TargetExecutable);
        args[5] = @"..\config.ini";
        Assert.False(UpdateHandoff.TryParseHelper(args, paths.HelperExecutable, out _));
        args[5] = "OneTalkText.exe";
        Assert.False(UpdateHandoff.TryParseHelper(args, workspace.File("elsewhere.exe"), out _));
    }

    private static string Release(string tag, bool prerelease = false, string? url = null, string? digest = null) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease,
            assets = new[] { new { name = GitHubUpdateClient.AssetName, state = "uploaded", size = Payload.Length,
                digest = digest ?? "sha256:" + Hash, browser_download_url = url ?? $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/{tag}/OneTalkText.exe" } }
        });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
    private sealed class InlineProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "update-tests"));
        private readonly string _path;
        public TestWorkspace() { _path = Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_path); }
        public string File(string name) => Path.Combine(_path, name);
        public void Dispose()
        {
            var full = Path.GetFullPath(_path);
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
