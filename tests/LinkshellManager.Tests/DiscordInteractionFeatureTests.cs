using Xunit;
using H = LinkshellManager.Tests.DiscordInteractionHarness;

namespace LinkshellManager.Tests;

// One readable check per feature of the Discord bot, through the real interaction endpoint: the
// response TYPE Discord gets (4 = message, 6 = quiet acknowledge, 7 = edit the clicked message,
// 9 = open a modal, 12 = launch the app) and whether it is private (flag 64).
//
// DiscordInteractionRoutingSnapshotTests pins every id in full; these say in plain terms what each
// area must keep doing, so a failure there can be read without diffing JSON.
public class DiscordInteractionFeatureTests
{
    private const int Message = 4;
    private const int QuietAck = 6;
    private const int EditClicked = 7;
    private const int OpenModal = 9;
    private const int LaunchApp = 12;
    private const int Private = 64;

    [Fact]
    public async Task Ping_GetsPong()
    {
        var outcome = await H.SendAsync(H.Bare(1));
        Assert.Equal(1, outcome.ResponseType);
    }

    [Fact]
    public async Task LaunchButton_AndEntryPointCommand_LaunchTheApp()
    {
        Assert.Equal(LaunchApp, (await H.SendAsync(H.Component("unlinked", "evt:launch"))).ResponseType);
        Assert.Equal(LaunchApp, (await H.SendAsync(H.Command("member", "launch"))).ResponseType);
    }

    [Fact]
    public async Task LsmCommand_PostsAPublicCardForAnOfficer_AndAPrivateRefusalOtherwise()
    {
        var officer = await H.SendAsync(H.Command("officer", "lsm"));
        Assert.Equal(Message, officer.ResponseType);
        Assert.Null(officer.Flags);

        var member = await H.SendAsync(H.Command("member", "lsm"));
        Assert.Equal(Message, member.ResponseType);
        Assert.Equal(Private, member.Flags);
    }

    // Sign-ups / party board: the board's Sign Up opens a private picker; a pick inside it edits
    // that picker; claiming a slot quietly acknowledges and deletes the picker.
    [Fact]
    public async Task PartyBoard_SignUpOpensAPrivatePicker_AndAClaimDismissesIt()
    {
        var signUp = await H.SendAsync(H.Component("member", "evt:pssignup:10"));
        Assert.Equal(Message, signUp.ResponseType);
        Assert.Equal(Private, signUp.Flags);

        var wizard = await H.SendAsync(H.Component("member", "evt:psclaim:10:0", new[] { "1" }, fromEphemeral: true));
        Assert.Equal(EditClicked, wizard.ResponseType);

        var claim = await H.SendAsync(H.Component("member", "evt:psclaim:10", new[] { "2" }, fromEphemeral: true));
        Assert.Equal(QuietAck, claim.ResponseType);
        Assert.Equal(1, claim.DismissCallbacks);
        Assert.Equal(new[] { 10 }, claim.Queued);
    }

    [Fact]
    public async Task PartyBoard_WithdrawRefreshesTheBoardQuietly()
    {
        var leave = await H.SendAsync(H.Component("member", "evt:psleave:10"));
        Assert.Equal(QuietAck, leave.ResponseType);
        Assert.Equal(0, leave.DismissCallbacks);
        Assert.Equal(0, leave.SlotSignups);
    }

    // HNM camp board: Manual Check In answers privately and queues a board refresh.
    [Fact]
    public async Task HnmCamp_CheckInAnswersPrivately()
    {
        var checkIn = await H.SendAsync(H.Component("member", "evt:xin:30"));
        Assert.Equal(Message, checkIn.ResponseType);
        Assert.Equal(Private, checkIn.Flags);
        Assert.Equal(new[] { 30 }, checkIn.Queued);
        Assert.Equal(1, checkIn.Participations);
    }

    [Fact]
    public async Task HnmCamp_WindowViewerPagesInPlace()
    {
        var page = await H.SendAsync(H.Component("unlinked", "evt:winview:10:1", fromEphemeral: true));
        Assert.Equal(EditClicked, page.ResponseType);
    }

    // End Camp: the button opens the modal for an officer only; a bad modal answer is a private error.
    [Fact]
    public async Task EndCamp_ButtonOpensTheModalForAnOfficerOnly()
    {
        Assert.Equal(OpenModal, (await H.SendAsync(H.Component("officer", "evt:wdpop:10"))).ResponseType);

        var member = await H.SendAsync(H.Component("member", "evt:wdpop:10"));
        Assert.Equal(Message, member.ResponseType);
        Assert.Equal(Private, member.Flags);

        var badRepost = await H.SendAsync(H.Modal("officer", "evt:wdpopmodal:10", ("wdpop_repost", "abc")));
        Assert.Equal(Message, badRepost.ResponseType);
        Assert.Equal(Private, badRepost.Flags);
        Assert.StartsWith("**Repost Event**", badRepost.Content);
    }

    // Loot / auctions: Bid opens the amount modal; the modal submit needs a linked account.
    [Fact]
    public async Task Auction_BidOpensAModal_AndTheSubmitNeedsAnAccount()
    {
        Assert.Equal(OpenModal, (await H.SendAsync(H.Component("member", "auc:bid:7"))).ResponseType);

        var unlinked = await H.SendAsync(H.Modal("unlinked", "auc:bidmodal:7", ("amount", "50")));
        Assert.Equal(Private, unlinked.Flags);
        Assert.StartsWith("Open LSM and sign in", unlinked.Content);
    }

    // Officer tools: refused privately for a member, a private picker for an officer, and a
    // finished move quietly dismisses the picker.
    [Fact]
    public async Task OfficerTools_AreGatedAndDismissWhenDone()
    {
        var member = await H.SendAsync(H.Component("member", "evt:omove:10"));
        Assert.Equal(Private, member.Flags);
        Assert.StartsWith("You need the **Manage parties** permission", member.Content);

        var officer = await H.SendAsync(H.Component("officer", "evt:omove:10"));
        Assert.Equal(Message, officer.ResponseType);
        Assert.Equal(Private, officer.Flags);

        var moved = await H.SendAsync(H.Component("officer", "evt:omovedst:10:s:2", new[] { "1" }, fromEphemeral: true));
        Assert.Equal(QuietAck, moved.ResponseType);
        Assert.Equal(1, moved.DismissCallbacks);
    }

    // Outside sign-up: with the linkshell setting off, nobody without an account gets in.
    [Fact]
    public async Task OutsideSignup_IsRefusedWhileTheSettingIsOff()
    {
        var click = await H.SendAsync(H.Component("unlinked", "evt:pssignup:10"));
        Assert.Equal(Private, click.Flags);
        Assert.StartsWith("This linkshell doesn't allow account-less signups.", click.Content);

        var modal = await H.SendAsync(H.Modal("unlinked", "evt:outsidename:slot:10", ("outside_name", "Newguy")));
        Assert.Equal(Private, modal.Flags);
        Assert.Equal("HNM outside sign-ups aren't enabled for this event.", modal.Content);
    }

    // The character picker resumes the flow it interrupted, editing the picker in place.
    [Fact]
    public async Task CharacterPick_ResumesTheInterruptedFlow()
    {
        var resumed = await H.SendAsync(H.Component("member", "evt:charpick:slot:10", new[] { "Mill" }, fromEphemeral: true));
        Assert.Equal(EditClicked, resumed.ResponseType);
        Assert.Contains("Pick a slot to claim", resumed.Content);
    }

    [Fact]
    public async Task UnknownIds_AreAnsweredPrivately()
    {
        var click = await H.SendAsync(H.Component("member", "evt:nosuchthing:10"));
        Assert.Equal("That action isn't recognized.", click.Content);
        Assert.Equal(Private, click.Flags);

        // A modal from an unlinked user is asked to sign in first, before the id is even looked at.
        var modal = await H.SendAsync(H.Modal("unlinked", "evt:nosuchmodal:10"));
        Assert.StartsWith("Open LSM and sign in", modal.Content);
    }
}
