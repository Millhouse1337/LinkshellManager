using System.Text.Json;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// A crash in an /api request must come back as JSON the app can read, not the HTML error
// page the app used to show as "your session may have expired". Pages keep the error page.
public class ApiExceptionHandlerTests
{
    private static DefaultHttpContext NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/activity/events/5/end")]
    [InlineData("/api/addon/events")]
    public async Task ApiCrash_Returns500Json_WithMessageAndCorrelationId(string path)
    {
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        var context = NewContext(path);

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("relation \"Secret\" does not exist"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);

        var body = await ReadBodyAsync(context);
        Assert.Equal(ApiExceptionHandler.ErrorMessage, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task ApiCrash_NeverEchoesTheExceptionText()
    {
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        var context = NewContext("/api/activity/overview");

        await handler.TryHandleAsync(
            context, new InvalidOperationException("relation \"Secret\" does not exist"), CancellationToken.None);

        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("Secret", raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
    }

    [Fact]
    public async Task EachCrash_GetsItsOwnCorrelationId()
    {
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        var first = NewContext("/api/activity/overview");
        var second = NewContext("/api/activity/overview");

        await handler.TryHandleAsync(first, new Exception("a"), CancellationToken.None);
        await handler.TryHandleAsync(second, new Exception("b"), CancellationToken.None);

        Assert.NotEqual(
            (await ReadBodyAsync(first)).GetProperty("correlationId").GetString(),
            (await ReadBodyAsync(second)).GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/features")]
    [InlineData("/discord-activity/events")]
    [InlineData("/apiary")]
    [InlineData("/Home/Error")]
    public async Task PageCrash_IsLeftForTheErrorPage(string path)
    {
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        var context = NewContext(path);

        var handled = await handler.TryHandleAsync(context, new Exception("boom"), CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(0, context.Response.Body.Length);
    }
}
