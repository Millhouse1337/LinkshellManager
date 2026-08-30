using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// Rules and announcements are written in Discord and pasted into LSManager, so the web
// pages render them with Discord's markdown dialect. These pin the shapes that dialect
// produces -- and that stored text can never smuggle markup onto the page.
public class DiscordMarkdownTests
{
    [Theory]
    [InlineData("# Big", "<h3 class=\"dc-h1\">Big</h3>")]
    [InlineData("## Medium", "<h4 class=\"dc-h2\">Medium</h4>")]
    [InlineData("### Small", "<h5 class=\"dc-h3\">Small</h5>")]
    public void Headings_StopAtThreeLevels(string source, string expected)
    {
        Assert.Equal(expected, DiscordMarkdown.ToHtml(source));
    }

    [Theory]
    [InlineData("#### Four")]      // Discord has no h4+; it stays text
    [InlineData("#NoSpace")]
    public void Headings_RequireOneToThreeHashesAndASpace(string source)
    {
        Assert.StartsWith("<p class=\"dc-p\">", DiscordMarkdown.ToHtml(source));
    }

    [Fact]
    public void Subtext_IsNotReadAsABullet()
    {
        Assert.Equal("<div class=\"dc-subtext\">fine print</div>", DiscordMarkdown.ToHtml("-# fine print"));
    }

    [Fact]
    public void Bullets_BecomeAList()
    {
        var html = DiscordMarkdown.ToHtml("- one\n- two");
        Assert.Equal("<ul class=\"dc-list\"><li>one</li><li>two</li></ul>", html);
    }

    [Fact]
    public void Bullets_NestByIndentation()
    {
        var html = DiscordMarkdown.ToHtml("- top\n  - nested\n- back");
        Assert.Equal(
            "<ul class=\"dc-list\"><li>top<ul class=\"dc-list\"><li>nested</li></ul></li><li>back</li></ul>",
            html);
    }

    [Fact]
    public void NumberedLists_UseOl()
    {
        var html = DiscordMarkdown.ToHtml("1. first\n2. second");
        Assert.Equal("<ol class=\"dc-list\"><li>first</li><li>second</li></ol>", html);
    }

    [Fact]
    public void SingleNewlines_StayHardBreaks_LikeDiscord()
    {
        Assert.Equal("<p class=\"dc-p\">one<br>two</p>", DiscordMarkdown.ToHtml("one\ntwo"));
    }

    [Fact]
    public void BlankLine_StartsANewParagraph()
    {
        Assert.Equal("<p class=\"dc-p\">one</p><p class=\"dc-p\">two</p>", DiscordMarkdown.ToHtml("one\n\ntwo"));
    }

    [Theory]
    [InlineData("**bold**", "<p class=\"dc-p\"><strong>bold</strong></p>")]
    [InlineData("*italic*", "<p class=\"dc-p\"><em>italic</em></p>")]
    [InlineData("***both***", "<p class=\"dc-p\"><strong><em>both</em></strong></p>")]
    [InlineData("__under__", "<p class=\"dc-p\"><u>under</u></p>")]
    [InlineData("~~gone~~", "<p class=\"dc-p\"><s>gone</s></p>")]
    public void Emphasis_MapsToDiscordsMarkers(string source, string expected)
    {
        Assert.Equal(expected, DiscordMarkdown.ToHtml(source));
    }

    [Fact]
    public void Emphasis_Nests()
    {
        Assert.Equal(
            "<p class=\"dc-p\"><strong>bold <em>and italic</em></strong></p>",
            DiscordMarkdown.ToHtml("**bold *and italic***"));
    }

    [Theory]
    [InlineData("2 * 3 * 4")]                    // spaced stars are arithmetic, not italics
    [InlineData("snake_case_name")]              // underscores inside a word stay literal
    [InlineData("a \\*not italic\\* b")]         // escaped markers stay literal
    public void Emphasis_DoesNotFireOnEverydayText(string source)
    {
        var html = DiscordMarkdown.ToHtml(source);
        Assert.DoesNotContain("<em>", html);
        Assert.DoesNotContain("<u>", html);
    }

    [Fact]
    public void Spoiler_IsRevealableRatherThanShown()
    {
        var html = DiscordMarkdown.ToHtml("||secret||");
        Assert.Contains("class=\"dc-spoiler\"", html);
        Assert.Contains(">secret</span>", html);
    }

    [Fact]
    public void InlineCode_IsNeverParsedInside()
    {
        Assert.Equal(
            "<p class=\"dc-p\"><code class=\"dc-code\">**not bold**</code></p>",
            DiscordMarkdown.ToHtml("`**not bold**`"));
    }

    [Fact]
    public void FencedCodeBlock_KeepsItsBodyVerbatim()
    {
        var html = DiscordMarkdown.ToHtml("```lua\nlocal x = 1\n```");
        Assert.Equal("<pre class=\"dc-codeblock\"><code>local x = 1</code></pre>", html);
    }

    [Fact]
    public void Quotes_GroupTheirRunOfLines()
    {
        var html = DiscordMarkdown.ToHtml("> quoted\n> still quoted\nafter");
        Assert.Equal(
            "<blockquote class=\"dc-quote\"><p class=\"dc-p\">quoted<br>still quoted</p></blockquote>"
            + "<p class=\"dc-p\">after</p>",
            html);
    }

    [Fact]
    public void TripleQuote_SwallowsTheRestOfThePost()
    {
        var html = DiscordMarkdown.ToHtml(">>> everything\nfrom here on");
        Assert.Equal(
            "<blockquote class=\"dc-quote\"><p class=\"dc-p\">everything<br>from here on</p></blockquote>",
            html);
    }

    [Fact]
    public void Links_AreMaskedOrBare_AndAlwaysSafeToClick()
    {
        var masked = DiscordMarkdown.ToHtml("[the rules](https://example.com/rules)");
        Assert.Contains("href=\"https://example.com/rules\"", masked);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", masked);
        Assert.Contains(">the rules</a>", masked);

        Assert.Contains("href=\"https://example.com\"", DiscordMarkdown.ToHtml("see https://example.com now"));
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](data:text/html,<script>alert(1)</script>)")]
    public void Links_RefuseNonHttpSchemes(string source)
    {
        var html = DiscordMarkdown.ToHtml(source);
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("<script", html);
    }

    [Theory]
    [InlineData("<@123456789012345678>", "@user")]
    [InlineData("<@!123456789012345678>", "@user")]
    [InlineData("<@&123456789012345678>", "@role")]
    [InlineData("<#123456789012345678>", "#channel")]
    [InlineData("@everyone", "@everyone")]
    public void Mentions_RenderAsPills(string source, string expected)
    {
        var html = DiscordMarkdown.ToHtml(source);
        Assert.Contains("class=\"dc-mention\"", html);
        Assert.Contains(expected, html);
    }

    [Fact]
    public void CustomEmoji_ResolveOffDiscordsCdn()
    {
        var html = DiscordMarkdown.ToHtml("<:hehe:123456789012345678>");
        Assert.Contains("src=\"https://cdn.discordapp.com/emojis/123456789012345678.png\"", html);
        Assert.Contains("alt=\":hehe:\"", html);

        var animated = DiscordMarkdown.ToHtml("<a:spin:123456789012345678>");
        Assert.Contains("123456789012345678.gif", animated);
    }

    [Fact]
    public void Timestamps_AreFormatted()
    {
        // 2021-01-01T00:00:00Z
        Assert.Contains("January 1, 2021", DiscordMarkdown.ToHtml("<t:1609459200:D>"));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "<img src=x")]
    [InlineData("**bold <b>tag</b>**", "<b>tag</b>")]
    public void StoredText_CanNeverInjectMarkup(string source, string forbidden)
    {
        Assert.DoesNotContain(forbidden, DiscordMarkdown.ToHtml(source));
    }

    [Fact]
    public void EmptyDetails_RenderNothing()
    {
        Assert.Equal(string.Empty, DiscordMarkdown.ToHtml(null));
        Assert.Equal(string.Empty, DiscordMarkdown.ToHtml("   "));
    }

    [Fact]
    public void ToPlainText_FlattensAPostForThePreviewRow()
    {
        var source = "## Welcome to Altana's Feet\nWe are a **HENM** linkshell.\n- Be respectful\n- Sign up early";
        Assert.Equal(
            "Welcome to Altana's Feet We are a HENM linkshell. • Be respectful • Sign up early",
            DiscordMarkdown.ToPlainText(source));
    }

    [Fact]
    public void ToPlainText_DropsMarkupAndDecodesEntities()
    {
        var text = DiscordMarkdown.ToPlainText("Loot & DKP <@123456789012345678> `code` [link](https://example.com)");
        Assert.Equal("Loot & DKP @user code link", text);
    }
}
