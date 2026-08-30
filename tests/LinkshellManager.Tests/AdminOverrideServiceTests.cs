using System.Reflection;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The app-wide admin override: full permissions in every linkshell a super admin is a
// MEMBER of, on top of (never replacing) their stored rank, behind a global on/off
// setting.
//
// The scoping invariant these tests exist to protect: the override must never, on its
// own, grant access to a linkshell the user has not joined. AdminOverrideService
// deliberately knows nothing about linkshells — every call site checks membership FIRST
// and only then consults it. If someone ever gives this service a linkshell-aware
// "grants everything" overload, that invariant moves from "structural" to "hope", and
// these tests will not catch it.
public class AdminOverrideServiceTests
{
    private const string SuperAdminId = "admin-1";
    private const string NormalUserId = "user-1";

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // A context seeded with one super admin, one ordinary user, and the toggle either
    // absent (never set) or set to the supplied value.
    private static async Task<AdminOverrideService> NewServiceAsync(bool? overrideEnabled)
    {
        var db = NewInMemoryContext();
        db.Users.Add(new AppUser { Id = SuperAdminId, UserName = "millhouse", IsSuperAdmin = true });
        db.Users.Add(new AppUser { Id = NormalUserId, UserName = "someone", IsSuperAdmin = false });
        await db.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new GlobalSettingsService(db, cache);
        if (overrideEnabled.HasValue)
        {
            await settings.SetAdminOverrideEnabledAsync(overrideEnabled.Value);
        }

        return new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance);
    }

    [Fact]
    public async Task NeverToggled_IsOff()
    {
        var service = await NewServiceAsync(overrideEnabled: null);

        Assert.False(await service.IsEnabledAsync());
        Assert.False(await service.IsActiveForAsync(SuperAdminId));
    }

    [Fact]
    public async Task ToggledOff_DoesNotApplyEvenToASuperAdmin()
    {
        var service = await NewServiceAsync(overrideEnabled: false);

        Assert.False(await service.IsActiveForAsync(SuperAdminId));
    }

    [Fact]
    public async Task ToggledOn_AppliesToASuperAdmin()
    {
        var service = await NewServiceAsync(overrideEnabled: true);

        Assert.True(await service.IsActiveForAsync(SuperAdminId));
    }

    // The toggle is app-wide, but it only ever elevates accounts already carrying
    // IsSuperAdmin — which is seeded from configuration and has no in-app write path.
    [Fact]
    public async Task ToggledOn_DoesNotApplyToAnOrdinaryUser()
    {
        var service = await NewServiceAsync(overrideEnabled: true);

        Assert.False(await service.IsActiveForAsync(NormalUserId));
    }

    [Fact]
    public async Task NullOrEmptyAppUserId_NeverApplies()
    {
        var service = await NewServiceAsync(overrideEnabled: true);

        Assert.False(await service.IsActiveForAsync((string?)null));
        Assert.False(await service.IsActiveForAsync(string.Empty));
        Assert.False(await service.IsActiveForAsync("   "));
    }

    [Fact]
    public async Task UnknownAppUserId_NeverApplies()
    {
        var service = await NewServiceAsync(overrideEnabled: true);

        Assert.False(await service.IsActiveForAsync("no-such-user"));
    }

    // The AppUser overload must agree with the id overload rather than trusting the
    // in-memory flag alone — otherwise a stale entity could elevate while the toggle
    // is off.
    [Fact]
    public async Task AppUserOverload_RespectsTheToggle()
    {
        var offService = await NewServiceAsync(overrideEnabled: false);
        var onService = await NewServiceAsync(overrideEnabled: true);
        var superAdmin = new AppUser { Id = SuperAdminId, IsSuperAdmin = true };
        var ordinary = new AppUser { Id = NormalUserId, IsSuperAdmin = false };

        Assert.False(await offService.IsActiveForAsync(superAdmin));
        Assert.True(await onService.IsActiveForAsync(superAdmin));
        Assert.False(await onService.IsActiveForAsync(ordinary));
        Assert.False(await onService.IsActiveForAsync((AppUser?)null));
    }

    // Belt-and-braces: a caller that forgets to check IsEnabledAsync still gets the
    // safe answer, because the id set is empty while the override is off. The roster
    // badge relies on this.
    [Fact]
    public async Task ActiveAdminIds_AreEmptyWhileTheOverrideIsOff()
    {
        var service = await NewServiceAsync(overrideEnabled: false);

        Assert.Empty(await service.GetActiveAdminAppUserIdsAsync());
    }

    [Fact]
    public async Task ActiveAdminIds_ContainOnlySuperAdminsWhileOn()
    {
        var service = await NewServiceAsync(overrideEnabled: true);

        var ids = await service.GetActiveAdminAppUserIdsAsync();

        Assert.Contains(SuperAdminId, ids);
        Assert.DoesNotContain(NormalUserId, ids);
    }

    // Turning the override off must actually restore normal permissions, not leave a
    // cached "on" behind on the instance that served the write.
    [Fact]
    public async Task TogglingOffTakesEffectImmediatelyOnTheWritingInstance()
    {
        using var db = NewInMemoryContext();
        db.Users.Add(new AppUser { Id = SuperAdminId, UserName = "millhouse", IsSuperAdmin = true });
        await db.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new GlobalSettingsService(db, cache);

        await settings.SetAdminOverrideEnabledAsync(true);
        Assert.True(await settings.IsAdminOverrideEnabledAsync());

        await settings.SetAdminOverrideEnabledAsync(false);
        Assert.False(await settings.IsAdminOverrideEnabledAsync());
    }

    // The two settings share one storage path now; prove they don't alias each other.
    [Fact]
    public async Task AdminOverrideAndAddonKillSwitchAreIndependent()
    {
        using var db = NewInMemoryContext();
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));

        await settings.SetAdminOverrideEnabledAsync(true);

        Assert.True(await settings.IsAdminOverrideEnabledAsync());
        Assert.False(await settings.IsAddonGloballyDisabledAsync());

        await settings.SetAddonGloballyDisabledAsync(true);
        await settings.SetAdminOverrideEnabledAsync(false);

        Assert.True(await settings.IsAddonGloballyDisabledAsync());
        Assert.False(await settings.IsAdminOverrideEnabledAsync());
    }

    // "Full permissions" has to keep meaning full permissions. Reflection rather than a
    // hand-written list, so a 21st permission added to LinkshellRole can't silently
    // arrive as false for the admin.
    [Fact]
    public void FullAccessRole_GrantsEveryPermissionFlag()
    {
        var role = LinkshellRoleDefaults.BuildFullAccessRole(linkshellId: 7);

        var falseFlags = typeof(LinkshellRole)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .Where(property => property.Name.StartsWith("Can", StringComparison.Ordinal))
            .Where(property => !(bool)property.GetValue(role)!)
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(falseFlags);
    }

    // The synthetic role is never persisted; an Id of 0 means an accidental Update()
    // throws instead of silently writing a real "Admin" row into someone's linkshell.
    [Fact]
    public void FullAccessRole_IsNotAPersistableRow()
    {
        var role = LinkshellRoleDefaults.BuildFullAccessRole(linkshellId: 7);

        Assert.Equal(0, role.Id);
        Assert.Equal(7, role.LinkshellId);
        Assert.Equal(LinkshellRoleDefaults.AdminRoleName, role.Name);
    }

    // "Admin" must not collide with a real assignable rank, or a member could be given
    // it by name in the roster editor and inherit the override's permissions without
    // being a super admin.
    [Fact]
    public void AdminRoleName_IsNotOneOfTheBuiltInRanks()
    {
        var builtIn = LinkshellRoleDefaults.BuildDefaultRoles(linkshellId: 1).Select(role => role.Name);

        Assert.DoesNotContain(LinkshellRoleDefaults.AdminRoleName, builtIn);
    }

    // The wire-level all-true payload the Activity SPA receives has to stay in step with
    // the server-side full-access role, or the UI would re-lock surfaces the API allows.
    [Fact]
    public void ActivityPermissionsAll_GrantsEveryFlag()
    {
        var falseFlags = typeof(ActivityPermissionsDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .Where(property => !(bool)property.GetValue(ActivityPermissions.All)!)
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(falseFlags);
    }

    // Every permission the DTO exposes must actually be granted by the role the server
    // hands out, so the two can't drift apart when a permission is added.
    [Fact]
    public void ActivityPermissionsAll_CoversTheSameFlagsAsTheFullAccessRole()
    {
        var dtoFlags = typeof(ActivityPermissionsDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var roleFlags = typeof(LinkshellRole)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .Where(property => property.Name.StartsWith("Can", StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The DTO intentionally omits the two approval-submission flags, which are
        // enforced server-side only and never surfaced to the SPA.
        Assert.Empty(dtoFlags.Except(roleFlags));
    }
}
