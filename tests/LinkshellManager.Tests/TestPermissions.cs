using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinkshellManager.Tests;

// A real LinkshellPermissions over a test database, for tests that build a controller or service
// by hand. The admin override is off unless the test switches it on through the same
// GlobalSettingsService (SetAdminOverrideEnabledAsync) on this database.
internal static class TestPermissions
{
    internal static LinkshellPermissions For(ApplicationDbContext db)
    {
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        return new LinkshellPermissions(
            db, new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance));
    }
}
