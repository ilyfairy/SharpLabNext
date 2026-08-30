using Microsoft.AspNetCore.Http;
using SharpLabNext.Http;

namespace SharpLabNext.UnitTests;

public sealed class PascalCaseQueryTests
{
    [Fact]
    public void OptionalIntegerRequiresExactPascalCaseKey()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?FromSequence=4");

        Assert.True(PascalCaseQuery.TryGetOptionalInt64(context.Request, "FromSequence", out var value));
        Assert.Equal(4, value);

        context.Request.QueryString = new QueryString("?fromSequence=4");

        Assert.False(PascalCaseQuery.TryGetOptionalInt64(context.Request, "FromSequence", out value));
        Assert.Null(value);
    }

    [Fact]
    public void OptionalSingleRejectsDuplicateAndWrongCaseKeys()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?ReturnPath=%2F%23one&ReturnPath=%2F%23two");

        Assert.False(PascalCaseQuery.TryGetOptionalSingle(context.Request, "ReturnPath", out var value));
        Assert.Null(value);

        context.Request.QueryString = new QueryString("?returnPath=%2F%23one");
        Assert.False(PascalCaseQuery.TryGetOptionalSingle(context.Request, "ReturnPath", out value));
        Assert.Null(value);
    }
}
