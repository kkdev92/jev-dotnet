using System.Text;
using Kkdev92.Jev.Responses;

namespace Kkdev92.Jev.Tests;

public sealed class ErrorBodyTests
{
    private static (string? ErrorType, IReadOnlyList<JevValidationError> Errors) Parse(string body) => ErrorBody.Parse(Encoding.UTF8.GetBytes(body));

    [Fact]
    public void TheObservedAuthenticationShapeYieldsItsErrorType()
    {
        var (errorType, errors) = Parse("""{"detail":{"error_type":"authentication_error","message":"Must supply an API key! Check your request and try again."}}""");

        Assert.Equal("authentication_error", errorType);
        Assert.Empty(errors);
    }

    [Fact]
    public void TheContractsValidationShapeYieldsTypesAndLocationsOnly()
    {
        var (errorType, errors) = Parse("""{"detail":[{"loc":["body","questions","urgency","criteria",0],"msg":"too short (SECRET)","type":"too_short","input":"SECRET","ctx":{"min_length":1}},{"loc":["body","state"],"msg":"Field required","type":"missing"}]}""");

        Assert.Null(errorType);
        Assert.Equal(["too_short", "missing"], errors.Select(e => e.Type));
        Assert.Equal(["body", "questions", "urgency", "criteria", "0"], errors[0].Location);
        Assert.DoesNotContain(errors, e => e.ToString().Contains("SECRET", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"detail":{"error_type":"Authentication Error"}}""")]
    [InlineData("""{"detail":{"error_type":"x<script>"}}""")]
    [InlineData("""{"detail":{"error_type":42}}""")]
    [InlineData("""{"detail":{"error_type":"_leading"}}""")]
    public void AnErrorTypeThatDoesNotLookLikeACodeIsDropped(string body) => Assert.Null(Parse(body).ErrorType);

    [Fact]
    public void AValidationEntryWithoutACodeShapedTypeIsSkipped()
    {
        var (_, errors) = Parse("""{"detail":[{"loc":["body"],"type":"Has Spaces"},{"loc":["body"],"type":"missing"},{"loc":["body"]}]}""");
        Assert.Equal("missing", Assert.Single(errors).Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("""{"detail":"Too many requests"}""")]
    [InlineData("""{"detail":[1,2""")]
    [InlineData("[]")]
    [InlineData("""{"error":{"message":"x"}}""")]
    public void AnythingElseYieldsNothingAndDoesNotThrow(string body)
    {
        var (errorType, errors) = Parse(body);

        Assert.Null(errorType);
        Assert.Empty(errors);
    }

    [Fact]
    public void InvalidUtf8YieldsNothing()
    {
        byte[] body = [.. "{\"detail\":{\"error_type\":\""u8, 0xFF, .. "\"}}"u8];
        Assert.Null(ErrorBody.Parse(body).ErrorType);
    }
}
