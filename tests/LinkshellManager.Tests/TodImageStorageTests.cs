using System;
using System.IO;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Where ToD screenshots live, who may upload one, and how many a day.
//
// Screenshots used to be saved inside the published app (wwwroot/uploads), which every deploy
// replaces, so they vanished with each release. They now live under the Uploads:Root setting.
// Uploading also used to need nothing but a signed-in account and had no limit, so anyone could
// fill the disk; it now needs a role that can put a ToD somewhere, and a daily cap per user.
public class TodImageStorageTests
{
    [Fact]
    public void ConfiguredRoot_WinsOverWwwroot()
    {
        var root = TodImageUploadService.ResolveUploadsRoot(
            "/var/lib/lsmanager/uploads", "/var/www/lsmanager/wwwroot", "/var/www/lsmanager");

        Assert.Equal("/var/lib/lsmanager/uploads", root);
    }

    [Fact]
    public void BlankRoot_FallsBackToWwwrootUploads_SoDevKeepsWorking()
    {
        var webRoot = Path.Combine("app", "wwwroot");

        Assert.Equal(Path.Combine(webRoot, "uploads"),
            TodImageUploadService.ResolveUploadsRoot("  ", webRoot, "app"));
        Assert.Equal(Path.Combine("app", "wwwroot", "uploads"),
            TodImageUploadService.ResolveUploadsRoot(null, null, "app"));
    }

    [Fact]
    public void StoredFiles_AreFoundAndDeleted_UnderTheConfiguredRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lsm-uploads-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tods = Path.Combine(root, "tods");
            Directory.CreateDirectory(tods);
            File.WriteAllBytes(Path.Combine(tods, "abc.png"), new byte[] { 1 });
            var service = new TodImageUploadService(root);

            Assert.Equal(Path.Combine(tods, "abc.png"), service.FindExistingFile("abc.png"));
            Assert.Null(service.FindExistingFile("missing.png"));

            service.DeleteIfExists("abc.png");
            Assert.False(File.Exists(Path.Combine(tods, "abc.png")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("..")]
    [InlineData("sub/abc.png")]
    [InlineData("sub\\abc.png")]
    [InlineData("C:abc.png")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsafeFileNames_AreRefused(string? fileName)
    {
        Assert.False(TodImageUploadService.IsSafeFileName(fileName));
    }

    [Fact]
    public void Upload_NeedsARoleThatCanPutAToDSomewhere()
    {
        Assert.False(ActivityWindowEventsController.RoleMayUploadTodImage(new LinkshellRole()));
        Assert.True(ActivityWindowEventsController.RoleMayUploadTodImage(new LinkshellRole { CanManageTods = true }));
        Assert.True(ActivityWindowEventsController.RoleMayUploadTodImage(new LinkshellRole { CanSubmitTodForApproval = true }));
        // HNM board "Post ToD" needs Manage events, and its form can attach a screenshot.
        Assert.True(ActivityWindowEventsController.RoleMayUploadTodImage(new LinkshellRole { CanManageEvents = true }));
    }

    [Fact]
    public void Quota_StopsAtTheDailyLimit_PerUser()
    {
        var quota = new TodImageUploadQuota(() => new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc));

        for (var i = 0; i < TodImageUploadQuota.DailyLimitPerUser; i++)
        {
            Assert.True(quota.TryTake("alice"));
        }

        Assert.False(quota.TryTake("alice"));
        Assert.True(quota.TryTake("bob"));
    }

    [Fact]
    public void Quota_RefusedFileGivesItsSlotBack()
    {
        var quota = new TodImageUploadQuota(() => new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < TodImageUploadQuota.DailyLimitPerUser; i++)
        {
            quota.TryTake("alice");
        }

        quota.Release("alice");

        Assert.True(quota.TryTake("alice"));
        Assert.False(quota.TryTake("alice"));
    }

    [Fact]
    public void Quota_ResetsAtTheNextUtcDay()
    {
        var now = new DateTime(2026, 11, 11, 23, 59, 0, DateTimeKind.Utc);
        var quota = new TodImageUploadQuota(() => now);
        for (var i = 0; i < TodImageUploadQuota.DailyLimitPerUser; i++)
        {
            quota.TryTake("alice");
        }
        Assert.False(quota.TryTake("alice"));

        now = now.AddMinutes(2);

        Assert.True(quota.TryTake("alice"));
    }
}
