using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

// deploy.ps1 runs the tests before every deploy, so this is what stops a model change that has
// no migration from reaching production (startup would migrate to a schema that does not match
// the code). It compares the model against the last migration's snapshot using the real Npgsql
// provider; building the model needs no database and the connection is never opened.
public class MigrationsUpToDateTests
{
    [Fact]
    public void EveryModelChangeHasAMigration()
    {
        // Program.cs's Identity setup caps the Identity key columns at 128 chars. The model reads
        // that from the app's services, so register it the same way here or the check sees four
        // phantom column changes on AspNetUserLogins/AspNetUserTokens.
        var appServices = new ServiceCollection();
        appServices.AddLogging();
        appServices.AddAppIdentity();
        using var provider = appServices.BuildServiceProvider();

        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=pending_model_check")
            .UseApplicationServiceProvider(provider)
            // A model cached by another test (built without the Identity options) must not be reused.
            .EnableServiceProviderCaching(false)
            .Options);

        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The EF model has changes with no migration. Run 'dotnet ef migrations add <Name>' (see CONTRIBUTING.md).");
    }
}
