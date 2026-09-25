using System.Globalization;
using System.Net;
using System.Text;

namespace Kkdev92.Jev.TestSupport;

/// <summary>Canned HTTP responses, shaped like the ones the service sends.</summary>
public static class FakeResponses
{
    /// <summary>The request id the fakes attach, in the shape the service uses: <c>req_</c> and 32 hex digits.</summary>
    public const string RequestId = "req_01a0caf71f397499b5c738884f2ce455";

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, bool withRequestId = true)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
            Version = HttpVersion.Version20,
        };

        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        if (withRequestId)
        {
            response.Headers.TryAddWithoutValidation("x-typesafe-request-id", RequestId);
        }

        return response;
    }

    public static HttpResponseMessage Bytes(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return response;
    }

    /// <summary>The observed body of a request without a key: status 403.</summary>
    public static HttpResponseMessage MissingKey() => Json(
        """{"detail":{"error_type":"authentication_error","message":"Must supply an API key! Check your request and try again."}}""",
        HttpStatusCode.Forbidden);

    /// <summary>The observed body of a request naming a model that does not exist: status 400.</summary>
    public static HttpResponseMessage UnknownModel() => Json(
        """{"detail":{"error_type":"api_usage_error","message":"Unknown model: jev-does-not-exist"}}""",
        HttpStatusCode.BadRequest);

    /// <summary>The observed body of a request with an invalid key: status 401.</summary>
    public static HttpResponseMessage InvalidKey() => Json(
        """{"detail":{"error_type":"authentication_error","message":"Cannot authenticate with the server. Please check your API key and try again."}}""",
        HttpStatusCode.Unauthorized);

    public static HttpResponseMessage RateLimited(string? retryAfter = null, string? retryAfterMilliseconds = null)
    {
        var response = Json("""{"detail":"Too many requests"}""", HttpStatusCode.TooManyRequests);

        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        if (retryAfterMilliseconds is not null)
        {
            response.Headers.TryAddWithoutValidation("retry-after-ms", retryAfterMilliseconds);
        }

        return response;
    }

    public static HttpResponseMessage Overloaded() => Json("""{"detail":"Overloaded"}""", (HttpStatusCode)529);

    /// <summary>A 422 whose body echoes a secret-looking input, to prove the SDK does not repeat it.</summary>
    public static HttpResponseMessage ValidationFailed(string echoedInput = "SECRET-INPUT-sk-live-123") => Json(
        """{"detail":[{"loc":["body","questions","urgency","criteria"],"msg":"List should have at least 1 item after validation, not 0 (ECHO)","type":"too_short","input":"ECHO","ctx":{"min_length":1}},{"loc":["body","state"],"msg":"Field required","type":"missing"}]}"""
            .Replace("ECHO", echoedInput, StringComparison.Ordinal),
        HttpStatusCode.UnprocessableEntity);

    public static string Models() =>
        """{"models":[{"name":"jev-latest","description":"General-purpose system one model.","release_date":"2026-09-15"},{"name":"jev-preview","description":"Most recent release.","release_date":"2026-09-15"}]}""";
}

/// <summary>Builds <c>POST /v1/systemone</c> response bodies answer by answer.</summary>
public sealed class SystemOneResponseBuilder
{
    private readonly List<string> _answers = [];
    private string _model = "jev-1.13.0";
    private string _usage = """{"input_tokens":120,"output_tokens":12}""";

    public SystemOneResponseBuilder Model(string model)
    {
        _model = model;
        return this;
    }

    public SystemOneResponseBuilder Usage(string usageJson)
    {
        _usage = usageJson;
        return this;
    }

    public SystemOneResponseBuilder Noul(string id, double probability)
        => Raw(id, $$"""{"type":"noul","noul":{{N(probability)}}}""");

    public SystemOneResponseBuilder Choice(string id, string choice, double confidence, params (string Label, double Probability)[] probabilities)
        => Raw(id, $$"""{"type":"choice","choice":{{Q(choice)}},"confidence":{{N(confidence)}},"probabilities":{{Map(probabilities)}}}""");

    public SystemOneResponseBuilder Score(string id, double score, double confidence, string[] legend, params double[] probabilities)
    {
        var legendJson = "{" + string.Join(",", legend.Select((l, i) => $"\"{i.ToString(CultureInfo.InvariantCulture)}\":{Q(l)}")) + "}";
        var probabilitiesJson = "{" + string.Join(",", probabilities.Select((p, i) => $"\"{i.ToString(CultureInfo.InvariantCulture)}\":{N(p)}")) + "}";
        return Raw(id, $$"""{"type":"score","score":{{N(score)}},"confidence":{{N(confidence)}},"legend":{{legendJson}},"probabilities":{{probabilitiesJson}}}""");
    }

    /// <summary>An answer written verbatim, for bodies no builder method produces.</summary>
    public SystemOneResponseBuilder Raw(string id, string answerJson)
    {
        _answers.Add($"{Q(id)}:{answerJson}");
        return this;
    }

    public string Build() => "{\"model\":" + Q(_model) + ",\"answers\":{" + string.Join(",", _answers) + "},\"usage\":" + _usage + "}";

    private static string Map((string Label, double Probability)[] probabilities)
        => "{" + string.Join(",", probabilities.Select(p => $"{Q(p.Label)}:{N(p.Probability)}")) + "}";

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Q(string value)
    {
        var builder = new StringBuilder("\"");

        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }
}
