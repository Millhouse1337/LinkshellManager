using System.IO;
using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// /healthz reports the deployed commit from version.txt (written by deploy.ps1). These pin
// that a deployed build shows its commit and a local run (no file) still answers.
public class AppVersionTests
{
    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lsm-appversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ReadsTheCommitFromVersionTxt_TrimmingTheNewline()
    {
        var dir = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, AppVersion.FileName), "3f2a9c1\r\n");
            Assert.Equal("3f2a9c1", AppVersion.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NoVersionTxt_ReportsUnknown()
    {
        var dir = NewFolder();
        try
        {
            Assert.Equal(AppVersion.Unknown, AppVersion.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmptyVersionTxt_ReportsUnknown()
    {
        var dir = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, AppVersion.FileName), "  \n");
            Assert.Equal(AppVersion.Unknown, AppVersion.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
