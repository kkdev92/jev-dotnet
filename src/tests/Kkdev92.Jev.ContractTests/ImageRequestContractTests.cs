using System.Text.Json;
using Kkdev92.Jev.TestSupport;
using Kkdev92.Jev.Wire;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

/// <summary>Requests that show images: the form, the header TypeSafe asks for, and what is refused before anything is sent.</summary>
public sealed class ImageRequestContractTests
{
    private const string ImagesHeader = "TypeSafe-Danger-Zone-Images";

    private static readonly byte[] Png = TestImages.Png(320, 320, (20, 20, 100));

    [Fact]
    public async Task AStateThatShowsAnImageIsSentAsAFormWithTheImagesHeader()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();
        var image = JevImage.FromBytes(Png);
        var state = $"The customer attached {image.Reference}. Is it a receipt?";

        await Create(handler).EvaluateAsync(state, [image], plan, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        var parts = FormBody.Parse(request.Body, request.ContentHeaders["Content-Type"]);

        Assert.Equal("true", request.Headers[ImagesHeader]);
        Assert.Equal(2, parts.Count);
        Assert.Equal("form-data; name=\"request\"", parts[0].Header("Content-Disposition"));
        Assert.Equal("application/json", parts[0].Header("Content-Type"));
        Assert.Equal("form-data; name=\"image-1\"; filename=\"image-1.png\"", parts[1].Header("Content-Disposition"));
        Assert.Equal("image/png", parts[1].Header("Content-Type"));
        Assert.Equal(Png, parts[1].Content);

        // The JSON part is a request the generated models accept: the state as given, and the
        // image's part named under its reference.
        var json = JsonSerializer.Deserialize(parts[0].Content, JevWireJsonContext.Default.SystemOneRequest)!;
        var validation = new WireValidation();
        json.Validate(validation, "$");

        Assert.True(validation.IsValid, string.Join("; ", validation.Violations));
        Assert.Equal(state, json.State.GetString());
        Assert.Equal(["tone", "urgent"], json.Questions.Keys);
        Assert.Equal([image.Reference], json.Assets!.Keys);
        Assert.Equal("image", json.Assets[image.Reference].Type);
        Assert.Equal("image-1", json.Assets[image.Reference].Part);
    }

    [Fact]
    public async Task ThePlansImagesComeFirstAndEachImageIsSentOnce()
    {
        var badge = JevImage.FromBytes(Png);
        var photo = JevImage.FromBytes(TestImages.JpegSignatureOnly());
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("same", $"Is the person in the photo the one on the badge, {badge.Reference}?");
        builder.AddImage(badge);
        var plan = builder.Build();
        var handler = FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Noul("same", 0.9).Build());

        // The plan's image passed again, and the photo twice: each is sent once, the plan's first.
        await Create(handler).EvaluateAsync($"Photo: {photo.Reference}", [photo, badge, photo], plan, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        var parts = FormBody.Parse(request.Body, request.ContentHeaders["Content-Type"]);
        var json = JsonSerializer.Deserialize(parts[0].Content, JevWireJsonContext.Default.SystemOneRequest)!;

        Assert.Equal(3, parts.Count);
        Assert.Equal(Png, parts[1].Content);
        Assert.Equal("form-data; name=\"image-2\"; filename=\"image-2.jpg\"", parts[2].Header("Content-Disposition"));
        Assert.Equal("image/jpeg", parts[2].Header("Content-Type"));
        Assert.Equal(TestImages.JpegSignatureOnly(), parts[2].Content);
        Assert.Equal("image-1", json.Assets![badge.Reference].Part);
        Assert.Equal("image-2", json.Assets[photo.Reference].Part);
    }

    [Fact]
    public async Task ABoxQuestionAboutThePlansImageNeedsNoImagesInTheCall()
    {
        var image = JevImage.FromBytes(Png);
        var builder = new JevDecisionPlanBuilder();
        var box = builder.AddBox("square", JevContent.FromJson($$"""{"find":"the black square","in":"{{image.Reference}}"}"""));
        builder.AddImage(image);
        var plan = builder.Build();
        var handler = FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Box("square", 0.0625, 0.375, 0.0625, 0.375, 0.9).Build());

        var result = await Create(handler).EvaluateAsync("A scanned page.", plan, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("true", request.Headers[ImagesHeader]);
        Assert.Equal(2, FormBody.Parse(request.Body, request.ContentHeaders["Content-Type"]).Count);

        var answer = result.Get(box);
        Assert.Equal(0.0625, answer.Top);
        Assert.Equal(0.375, answer.Bottom);
        Assert.Equal(0.0625, answer.Left);
        Assert.Equal(0.375, answer.Right);
        Assert.Equal(0.9, answer.Confidence);
        Assert.Empty(result.Metadata.Warnings);
    }

    [Fact]
    public async Task ABoxWhoseEdgesCrossIsReturnedAsSentWithAWarning()
    {
        var image = JevImage.FromBytes(Png);
        var builder = new JevDecisionPlanBuilder();
        var box = builder.AddBox("square", "the black square");
        var plan = builder.Build();
        var handler = FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Box("square", 0.6, 0.3, 0.1, 0.2, null).Build());

        var result = await Create(handler).EvaluateAsync($"The page: {image.Reference}", [image], plan, cancellationToken: TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Metadata.Warnings);
        Assert.Equal(JevConsistencyCheck.BoxEdgeOrder, warning.Check);
        Assert.Equal("square", warning.QuestionId);
        Assert.Equal(0.6, result.Get(box).Top);
        Assert.Equal(0.3, result.Get(box).Bottom);
        Assert.Null(result.Get(box).Confidence);
    }

    [Fact]
    public async Task ABoxQuestionWithNoImageToLookAtIsRefusedBeforeTheCallStarts()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var builder = new JevDecisionPlanBuilder();
        builder.AddBox("square", "the black square");
        var plan = builder.Build();
        var client = Create(handler);
        var token = TestContext.Current.CancellationToken;

        // The overloads without images name the plan; the ones with images name the empty list.
        Assert.Equal("plan", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateAsync("A scanned page.", plan, cancellationToken: token); }).ParamName);
        Assert.Equal("plan", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateContentAsync(JevContent.FromJson("""{"page":1}"""), plan, cancellationToken: token); }).ParamName);
        Assert.Equal("plan", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateAsync(new RequestContractTests.Ticket("Scan", []), ContractJsonContext.Default.Ticket, plan, cancellationToken: token); }).ParamName);
        Assert.Equal("images", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateAsync("A scanned page.", [], plan, cancellationToken: token); }).ParamName);
        Assert.Equal("images", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateContentAsync(JevContent.FromJson("""{"page":1}"""), [], plan, cancellationToken: token); }).ParamName);

        await Task.Yield();
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ImagesAndReferencesThatDoNotMatchAreRefusedBeforeTheCallStarts()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();
        var image = JevImage.FromBytes(Png);
        var client = Create(handler);
        var token = TestContext.Current.CancellationToken;

        // An image passed with the call that the state never shows.
        Assert.Equal("images", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateAsync("No picture here.", [image], plan, cancellationToken: token); }).ParamName);

        // A reference whose image the request would not send.
        Assert.Equal("state", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateAsync($"See {image.Reference}.", plan, cancellationToken: token); }).ParamName);
        Assert.Equal("state", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateContentAsync(JevContent.FromJson($$"""{"photo":"{{image.Reference}}"}"""), plan, cancellationToken: token); }).ParamName);

        // A reference in a property name stays text to the service, so it does not show the image.
        Assert.Equal("images", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateContentAsync(JevContent.FromJson($$"""{"{{image.Reference}}":"photo"}"""), [image], plan, cancellationToken: token); }).ParamName);

        // A null list, or a null entry in one.
        Assert.Equal("images", Assert.Throws<ArgumentNullException>(() => { _ = client.EvaluateAsync("text", (IReadOnlyList<JevImage>)null!, plan, cancellationToken: token); }).ParamName);
        Assert.Equal("images", Assert.Throws<ArgumentException>(() => { _ = client.EvaluateAsync($"See {image.Reference}.", [image, null!], plan, cancellationToken: token); }).ParamName);

        await Task.Yield();
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ATypedStateIsCheckedOnceSerializedAndAMismatchFailsTheTask()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();
        var image = JevImage.FromBytes(Png);
        var stray = JevImage.FromBytes(Png);
        var client = Create(handler);
        var token = TestContext.Current.CancellationToken;

        var notShown = client.EvaluateAsync(new RequestContractTests.Ticket("Receipt", ["see the attachment"]), ContractJsonContext.Default.Ticket, [image], plan, cancellationToken: token);
        Assert.Equal("images", (await Assert.ThrowsAsync<ArgumentException>(() => notShown)).ParamName);

        var notSent = client.EvaluateAsync(new RequestContractTests.Ticket("Receipt", [stray.Reference]), ContractJsonContext.Default.Ticket, plan, cancellationToken: token);
        Assert.Equal("state", (await Assert.ThrowsAsync<ArgumentException>(() => notSent)).ParamName);

        Assert.Empty(handler.Requests);

        await client.EvaluateAsync(new RequestContractTests.Ticket("Receipt", [image.Reference]), ContractJsonContext.Default.Ticket, [image], plan, cancellationToken: token);

        var request = Assert.Single(handler.Requests);
        var parts = FormBody.Parse(request.Body, request.ContentHeaders["Content-Type"]);
        using var json = JsonDocument.Parse(parts[0].Content);

        Assert.Equal(image.Reference, json.RootElement.GetProperty("state").GetProperty("messages")[0].GetString());
        Assert.Equal(Png, parts[1].Content);
    }

    [Fact]
    public async Task ARetryResendsTheSameForm()
    {
        var image = JevImage.FromBytes(Png);
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(FakeResponses.Overloaded, () => FakeResponses.Json(Answer()));

        await Create(handler, o => o.AdditionalRetries = 1).EvaluateAsync($"See {image.Reference}.", [image], plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.Equal(handler.Requests[0].ContentHeaders["Content-Type"], handler.Requests[1].ContentHeaders["Content-Type"]);
        Assert.Equal(["true", "true"], handler.Requests.Select(r => r.Headers[ImagesHeader]));
    }

    [Fact]
    public async Task ImagesCountTowardsTheRequestLimit()
    {
        // Large enough that the limit can be set to its size: the image fits, the form around it does not.
        var image = JevImage.FromBytes(TestImages.Png(800, 800, (100, 100, 300)));
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always(Answer());

        var exception = await Assert.ThrowsAsync<JevLimitException>(() => Create(handler, o => o.MaxRequestBodyBytes = image.Length)
            .EvaluateAsync($"See {image.Reference}.", [image], plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevLimit.RequestBody, exception.Limit);
        Assert.Equal(JevRequestTransmission.NotStarted, exception.Transmission);
        Assert.Empty(handler.Requests);
    }
}
