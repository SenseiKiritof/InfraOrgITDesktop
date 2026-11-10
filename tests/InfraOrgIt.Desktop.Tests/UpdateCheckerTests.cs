using System.Net;
using System.Text;
using InfraOrgIt.Desktop.Update;

namespace InfraOrgIt.Desktop.Tests;

public sealed class UpdateCheckerTests
{
    private const string TagUrl =
        "https://github.com/SenseiKiritof/InfraOrgITDesktop/releases/tag/v0.1.1";

    [Fact]
    public async Task Check_requests_the_latest_release_with_a_user_agent()
    {
        var handler = new FakeHandler(
            _ => Json(HttpStatusCode.OK, Release("v0.1.1")));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        await checker.CheckAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.github.com/repos/SenseiKiritof/InfraOrgITDesktop/releases/latest",
            request.RequestUri!.ToString());
        Assert.Equal("InfraOrgIT-Desktop", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Check_reports_a_newer_release()
    {
        var handler = new FakeHandler(
            _ => Json(HttpStatusCode.OK, Release("v0.1.1")));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.True(result.IsNewer);
        Assert.False(result.Failed);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Equal("0.1.1", result.LatestVersion);
        Assert.Equal(TagUrl, result.ReleaseUrl!.AbsoluteUri);
        Assert.Equal("InfraOrgIT Desktop 0.1.1", result.ReleaseName);
        Assert.Equal("Note della release", result.ReleaseNotes);
        Assert.Contains("0.1.1", result.Message);
    }

    [Fact]
    public async Task Check_reports_the_current_version_when_up_to_date()
    {
        var handler = new FakeHandler(
            _ => Json(HttpStatusCode.OK, Release("v0.1.0")));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.False(result.IsNewer);
        Assert.False(result.Failed);
        Assert.Equal("0.1.0", result.LatestVersion);
        Assert.Null(result.ReleaseUrl);
        Assert.Contains("ultima versione", result.Message);
    }

    [Fact]
    public async Task Check_reports_no_releases_on_404()
    {
        var handler = new FakeHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.False(result.IsNewer);
        Assert.False(result.Failed);
        Assert.Null(result.LatestVersion);
        Assert.Contains("Nessuna release", result.Message);
    }

    [Fact]
    public async Task Check_reports_the_rate_limit()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = Json(
                HttpStatusCode.Forbidden,
                """{"message":"API rate limit exceeded"}""");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Remaining", "0");
            return response;
        });
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.True(result.Failed);
        Assert.False(result.IsNewer);
        Assert.Contains("Limite", result.Message);
    }

    [Fact]
    public async Task Check_reports_other_http_errors()
    {
        var handler = new FakeHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.True(result.Failed);
        Assert.Contains("502", result.Message);
    }

    [Fact]
    public async Task Check_reports_a_transport_failure_without_throwing()
    {
        var handler = new FakeHandler(
            _ => throw new HttpRequestException("offline"));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.True(result.Failed);
        Assert.Contains("GitHub", result.Message);
    }

    [Fact]
    public async Task Check_reports_an_invalid_payload_without_throwing()
    {
        var handler = new FakeHandler(
            _ => Json(HttpStatusCode.OK, """{"tag_name":""}"""));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.True(result.Failed);
        Assert.Contains("versione valida", result.Message);
    }

    [Fact]
    public async Task Check_ignores_a_prerelease_release()
    {
        var handler = new FakeHandler(
            _ => Json(
                HttpStatusCode.OK,
                Release("v0.2.0-rc.1", prerelease: true)));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.False(result.IsNewer);
        Assert.False(result.Failed);
        Assert.Null(result.ReleaseUrl);
        Assert.Contains("prerelease", result.Message);
    }

    [Fact]
    public async Task Check_falls_back_to_the_releases_page_for_a_non_https_url()
    {
        var handler = new FakeHandler(
            _ => Json(
                HttpStatusCode.OK,
                Release("v0.1.1", url: "javascript:alert(1)")));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.True(result.IsNewer);
        Assert.Equal(
            "https://github.com/SenseiKiritof/InfraOrgITDesktop/releases",
            result.ReleaseUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task Check_caps_long_release_notes()
    {
        var notes = new string('x', UpdateChecker.NotesLimit + 500);
        var handler = new FakeHandler(
            _ => Json(HttpStatusCode.OK, Release("v0.1.1", notes: notes)));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.0");

        var result = await checker.CheckAsync();

        Assert.NotNull(result.ReleaseNotes);
        Assert.True(result.ReleaseNotes!.Length <= UpdateChecker.NotesLimit + 1);
        Assert.EndsWith("…", result.ReleaseNotes);
    }

    [Theory]
    [InlineData("0.1.0", "v0.1.1", true)]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.9.9", "v1.0.0", true)]
    [InlineData("0.1.0", "V0.1.1", true)]
    [InlineData("0.1.0", "0.1.0+abc123", false)]
    [InlineData("0.1.0", "v0.1.0", false)]
    [InlineData("1.0.0", "v0.9.9", false)]
    [InlineData("0.1.0", "v0.2.0-beta.1", false)]
    [InlineData("0.1.0", "not-a-version", false)]
    [InlineData("0.1.0", "v0.1", false)]
    public void IsNewerVersion_compares_semver_and_skips_prereleases(
        string current,
        string latest,
        bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewerVersion(current, latest));
    }

    [Fact]
    public void Detect_current_version_returns_a_parseable_semver()
    {
        var value = UpdateChecker.DetectCurrentVersion();

        Assert.True(UpdateChecker.TryParseVersion(value, out _, out _));
        Assert.DoesNotContain("+", value);
    }

    [Fact]
    public void TryParseVersion_accepts_the_v_prefix_and_build_metadata()
    {
        Assert.True(UpdateChecker.TryParseVersion(
            "v1.2.3+build.7", out var version, out var isPrerelease));

        Assert.Equal(new Version(1, 2, 3), version);
        Assert.False(isPrerelease);
    }

    private static string Release(
        string tag,
        string? notes = "Note della release",
        bool prerelease = false,
        string? url = TagUrl) =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "name": "InfraOrgIT Desktop {{tag.TrimStart('v')}}",
          "html_url": "{{url}}",
          "body": "{{notes}}",
          "prerelease": {{(prerelease ? "true" : "false")}},
          "draft": false
        }
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FakeHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
