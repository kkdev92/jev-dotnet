using System.Text;
using System.Text.Json;
using Kkdev92.Jev.Requests;
using Kkdev92.Jev.TestSupport;
using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev.Tests;

/// <summary>Plans that show images or ask box questions, and the request bodies written for them.</summary>
public sealed class ImagePlanTests
{
    private static readonly byte[] Png = TestImages.Png(320, 320);

    private static JevDecisionPlan NoulPlan()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("n", "Is it a cat?");
        return builder.Build();
    }

    [Fact]
    public void ABoxQuestionIsEncodedWithItsTypeAndInstructionsAlone()
    {
        var builder = new JevDecisionPlanBuilder();
        var box = builder.AddBox("mug", "the coffee mug in the photo");
        var plan = builder.Build();

        Assert.Equal("""{"mug":{"type":"box","instructions":"the coffee mug in the photo"}}""", Encoding.UTF8.GetString(plan.EncodedQuestions));
        Assert.True(plan.HasBoxes);
        Assert.Empty(plan.Images);
        Assert.Equal("mug", box.Id);
        Assert.Equal("JevBoxHandle (mug)", box.ToString());
        Assert.True(default(JevBoxHandle).IsDefault);
        Assert.Equal("JevBoxHandle (default)", default(JevBoxHandle).ToString());
    }

    [Fact]
    public void ABoxIdAndItsInstructionsAreCheckedLikeAnyOther()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddBox("b", "the cat");

        Assert.Throws<ArgumentException>(() => builder.AddBox("b", "the dog"));
        Assert.Throws<ArgumentException>(() => builder.AddBox(string.Empty, "the dog"));
        Assert.Throws<ArgumentNullException>(() => builder.AddBox(null!, "the dog"));
        Assert.Throws<ArgumentException>(() => builder.AddBox("c", "bad" + (char)0xD800));
    }

    [Fact]
    public void AnImageAQuestionShowsIsKeptOnceInThePlan()
    {
        var image = JevImage.FromBytes(Png);
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("same", JevContent.FromJson($$"""{"question":"Is this the person on the badge?","badge":"{{image.Reference}}"}"""));
        builder.AddImage(image);
        builder.AddImage(image);

        var plan = builder.Build();

        Assert.Same(image, Assert.Single(plan.Images));
        Assert.False(plan.HasBoxes);
    }

    [Fact]
    public void AReferenceInACriterionOrAnOptionDescriptionShowsTheImage()
    {
        var (first, second, third) = (JevImage.FromBytes(Png), JevImage.FromBytes(Png), JevImage.FromBytes(Png));

        var choice = new JevDecisionPlanBuilder();
        choice.AddChoice<int>("c", "Which does it match?", [new(1, "this", $"looks like {first.Reference}"), new(2, "other")]);
        choice.AddImage(first);
        Assert.Same(first, Assert.Single(choice.Build().Images));

        var noul = new JevDecisionPlanBuilder();
        noul.AddNoul("n", "Is it?", new JevNoulCriteria(second.Reference, JevContent.Null));
        noul.AddImage(second);
        Assert.Same(second, Assert.Single(noul.Build().Images));

        var score = new JevDecisionPlanBuilder();
        score.AddScore("s", "How close is it?", ["far", JevContent.FromJson($$"""{"like":"{{third.Reference}}"}""")]);
        score.AddImage(third);
        Assert.Same(third, Assert.Single(score.Build().Images));
    }

    [Fact]
    public void AnImageNoQuestionShowsFailsTheBuild()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("n", "Is it urgent?");
        builder.AddImage(JevImage.FromBytes(Png));

        var exception = Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Contains("appears in no question", exception.Message, StringComparison.Ordinal);

        // As after any failed build, the builder is not used again.
        Assert.Throws<InvalidOperationException>(() => builder.AddNoul("m", "q"));
    }

    [Fact]
    public void AReferenceInAPropertyNameDoesNotShowTheImage()
    {
        // TypeSafe replaces references in string values only; in a key the reference stays text.
        var image = JevImage.FromBytes(Png);
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("n", JevContent.FromJson($$"""{"{{image.Reference}}":"Is this it?"}"""));
        builder.AddImage(image);

        Assert.Throws<InvalidOperationException>(builder.Build);
    }

    [Fact]
    public void AReferenceToAnImageThePlanWasNotGivenFailsTheBuild()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("n", $"Is {JevImage.FromBytes(Png).Reference} a cat?");

        var exception = Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Contains("was not given", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingIsRefusedOnceTheBuilderHasBuilt()
    {
        var builder = new JevDecisionPlanBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.AddImage(null!));

        builder.AddNoul("n", "q");
        builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.AddImage(JevImage.FromBytes(Png)));
        Assert.Throws<InvalidOperationException>(() => builder.AddBox("b", "the cat"));
    }

    [Fact]
    public async Task WithoutImagesTheBodyIsTheJsonAlone()
    {
        var body = RequestBodyWriter.WriteEvaluate("jev-latest", StateSource.FromContent("text"), NoulPlan(), [], 1 << 20);
        using var content = body.CreateContent();

        Assert.False(body.HasImages);
        Assert.Equal("application/json", content.Headers.ContentType!.ToString());
        Assert.Equal(body.Json, await content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("assets", Encoding.UTF8.GetString(body.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithImagesTheBodyIsAFormWithTheJsonFirstAndThePlansImagesBeforeTheCalls()
    {
        var shown = JevImage.FromBytes(Png);
        var stated = JevImage.FromBytes(TestImages.JpegSignatureOnly());
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("same", $"Is the person in the photo the one in {shown.Reference}?");
        builder.AddImage(shown);
        var plan = builder.Build();
        var state = StateSource.FromContent(JevContent.FromJson($$"""{"photo":"{{stated.Reference}}"}"""));

        var body = RequestBodyWriter.WriteEvaluate("jev-latest", state, plan, [stated], 1 << 20);
        using var content = body.CreateContent();
        var bytes = await content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var contentType = content.Headers.ContentType!.ToString();
        var parts = FormBody.Parse(bytes, contentType);

        Assert.True(body.HasImages);
        Assert.Matches("^jev-[0-9a-f]{32}$", FormBody.Boundary(contentType));
        Assert.Equal(body.Length, bytes.Length);
        Assert.Equal(body.Length, (int)content.Headers.ContentLength!.Value);
        Assert.Equal(3, parts.Count);

        Assert.Equal(["Content-Disposition: form-data; name=\"request\"", "Content-Type: application/json"], parts[0].HeaderLines);
        Assert.Equal(body.Json, parts[0].Content);
        Assert.Equal(["Content-Disposition: form-data; name=\"image-1\"; filename=\"image-1.png\"", "Content-Type: image/png"], parts[1].HeaderLines);
        Assert.Equal(Png, parts[1].Content);
        Assert.Equal(["Content-Disposition: form-data; name=\"image-2\"; filename=\"image-2.jpg\"", "Content-Type: image/jpeg"], parts[2].HeaderLines);
        Assert.Equal(TestImages.JpegSignatureOnly(), parts[2].Content);

        // The JSON is a request the generated models accept, naming each image's part under its reference.
        var request = JsonSerializer.Deserialize(body.Json, JevWireJsonContext.Default.SystemOneRequest)!;
        var validation = new WireValidation();
        request.Validate(validation, "$");

        Assert.True(validation.IsValid, string.Join("; ", validation.Violations));
        Assert.Equal([shown.Reference, stated.Reference], request.Assets!.Keys);
        Assert.Equal("image", request.Assets[shown.Reference].Type);
        Assert.Equal("image-1", request.Assets[shown.Reference].Part);
        Assert.Null(request.Assets[shown.Reference].Format);
        Assert.Null(request.Assets[shown.Reference].DataBase64);
        Assert.Equal("image-2", request.Assets[stated.Reference].Part);
    }

    [Fact]
    public void TheLimitIsOnTheWholeForm()
    {
        var image = JevImage.FromBytes(Png);
        var plan = NoulPlan();
        var state = StateSource.FromContent($"Is {image.Reference} a cat?");

        var length = RequestBodyWriter.WriteEvaluate("jev-latest", state, plan, [image], 1 << 20).Length;

        Assert.Equal(length, RequestBodyWriter.WriteEvaluate("jev-latest", state, plan, [image], length).Length);
        Assert.Equal(JevLimit.RequestBody, Assert.Throws<JevLimitException>(() => RequestBodyWriter.WriteEvaluate("jev-latest", state, plan, [image], length - 1)).Limit);

        // An image that leaves no room for the JSON at all.
        Assert.Throws<JevLimitException>(() => RequestBodyWriter.WriteEvaluate("jev-latest", state, plan, [image], Png.Length));
    }

    [Fact]
    public async Task EveryContentIsOverTheSameBytes()
    {
        var image = JevImage.FromBytes(Png);
        var body = RequestBodyWriter.WriteEvaluate("jev-latest", StateSource.FromContent(image.Reference), NoulPlan(), [image], 1 << 20);

        using var first = body.CreateContent();
        using var second = body.CreateContent();

        Assert.NotSame(first, second);
        Assert.Equal(await first.ReadAsByteArrayAsync(TestContext.Current.CancellationToken), await second.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(first.Headers.ContentType!.ToString(), second.Headers.ContentType!.ToString());
    }
}
