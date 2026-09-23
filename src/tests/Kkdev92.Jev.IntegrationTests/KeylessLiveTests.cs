using System.Net;
using System.Text;
using System.Text.Json;

namespace Kkdev92.Jev.IntegrationTests;

/// <summary>
/// Checks against the live service that need no key and cost nothing: how it refuses a request
/// with no key or a wrong one, and that the SDK reads that refusal as documented.
/// </summary>
/// <remarks>
/// <para>
/// Run only when <c>JEV_DOTNET_KEYLESS_LIVE</c> is <c>1</c>, and in CI only by hand, through
/// <c>live-keyless.yml</c>. Nothing here is billed — every request is refused before any model
/// runs — but each is still a request to somebody else's service. So none runs on a schedule, on a
/// pull request, or by default on a developer machine: a stream of unauthenticated requests from a
/// build farm is indistinguishable from probing.
/// </para>
/// <para>
/// What this pins is what the repository records as observed rather than documented
/// (<c>missing-key-status</c> in <c>spec/typesafe-v1/semantics.json</c>): <c>403</c> without a key
/// and <c>401</c> with a wrong one, where the HTTP reference documents <c>401</c> for both; the
/// <c>{"detail":{"error_type":…}}</c> body; the request id's shape; and HTTP/2. The SDK half — the
/// error type read, the request id kept, a <c>401</c> never retried, the key never repeated — is
/// also covered offline, against a fake; this is the same path against the real service, so a
/// change on TypeSafe's side shows up here first.
/// </para>
/// </remarks>
[Trait("Category", "LiveKeyless")]
public sealed class KeylessLiveTests
{
    private const string Variable = "JEV_DOTNET_KEYLESS_LIVE";

    /// <summary>A key that cannot be valid. Not a secret: it is refused before anything is billed.</summary>
    private const string WrongKey = "sk-jev-dotnet-keyless-check-invalid";

    private const string Body = """{"model":"jev-latest","state":"A keyless check.","questions":{"check":{"type":"noul","instructions":"Is this a check?"}}}""";

    private static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";

    public static TheoryData<string> Operations => ["GET /v1/models", "POST /v1/systemone"];

    /// <summary>
    /// A wrong key, sent through the SDK with retries on, is refused once with a <c>401</c> the SDK
    /// reads in full — and the key appears nowhere in what the SDK reports.
    /// </summary>
    [Theory]
    [MemberData(nameof(Operations))]
    public async Task AWrongKeyIsRefusedOnceAndNeverRepeated(string operation)
    {
        Assert.SkipUnless(Enabled, $"{Variable} is not 1; skipping keyless live checks.");

        var recording = new Recording();
        using var http = new HttpClient(new RecordingHandler(recording, new SocketsHttpHandler()));
        var client = new JevClient(http, new JevClientOptions
        {
            Credential = new StaticJevCredential(WrongKey),

            // On, so that "not retried" is a property of a 401 and not of the defaults.
            AdditionalRetries = 2,
        });

        var exception = await Assert.ThrowsAsync<JevHttpException>(() => operation.StartsWith("GET", StringComparison.Ordinal)
            ? (Task)client.GetModelsAsync(cancellationToken: TestContext.Current.CancellationToken)
            : client.EvaluateAsync("A keyless check.", Plan(), cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write($"{operation} with a wrong key", [.. LiveReport.Describe(exception), $"Requests sent: {recording.Requests}; over HTTP/{string.Join(", ", recording.Versions)}"]);

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("authentication_error", exception.ErrorType);
        Assert.True(LiveReport.IsRequestId(exception.RequestId), "The request id is missing or not of the shape req_ and 32 hex digits.");
        Assert.Equal(JevRequestTransmission.Started, exception.Transmission);
        Assert.Equal(1, recording.Requests);
        Assert.Equal(HttpVersion.Version20, Assert.Single(recording.Versions));
        Assert.DoesNotContain(WrongKey, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// No key at all is a <c>403</c>, not the <c>401</c> the HTTP reference documents.
    /// </summary>
    /// <remarks>
    /// The SDK cannot send this request: it refuses an empty key before anything leaves the
    /// process. So this asks with a bare <see cref="HttpClient"/>, to keep the observation
    /// <c>missing-key-status</c> relies on current.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Operations))]
    public async Task NoKeyIsRefusedWith403(string operation)
    {
        Assert.SkipUnless(Enabled, $"{Variable} is not 1; skipping keyless live checks.");

        var post = operation.StartsWith("POST", StringComparison.Ordinal);
        using var http = new HttpClient(new SocketsHttpHandler());
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, new Uri(JevClientOptions.DefaultBaseAddress, operation[(operation.IndexOf(' ', StringComparison.Ordinal) + 1)..]))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = post ? new StringContent(Body, Encoding.UTF8, "application/json") : null,
        };

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var requestId = response.Headers.TryGetValues("x-typesafe-request-id", out var values) ? values.SingleOrDefault() : null;

        string? errorType;

        try
        {
            using var document = JsonDocument.Parse(body);
            errorType = document.RootElement.GetProperty("detail").GetProperty("error_type").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            errorType = null;
        }

        LiveReport.Write($"{operation} with no key",
        [
            $"HTTP {(int)response.StatusCode} over HTTP/{response.Version}, error type `{errorType ?? "(not found)"}`",
            "Request id: " + (LiveReport.IsRequestId(requestId) ? "`req_` and 32 hex digits" : "**missing or not of the expected shape**"),
        ]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authentication_error", errorType);
        Assert.True(LiveReport.IsRequestId(requestId), "The request id is missing or not of the shape req_ and 32 hex digits.");
    }

    private static JevDecisionPlan Plan()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("check", "Is this a check?");
        return builder.Build();
    }
}
