using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using NodaTime;
using Xunit;

namespace LinkshellManager.Tests;

// A snapshot's rows are the evidence behind a DKP payout, so the addon's scanned block stays intact
// and alphabetical, and anything an officer typed in by hand collects underneath it.
public class SnapshotEntryOrderingTests
{
    private static readonly DateTimeZone Utc = DateTimeZoneProviders.Tzdb["UTC"];

    private static AttendanceSnapshotEntry Entry(int id, string name, bool manual = false) => new()
    {
        Id = id,
        CharacterName = name,
        AddedManually = manual,
    };

    private static AttendanceSnapshot SnapshotWith(params AttendanceSnapshotEntry[] entries)
    {
        var snapshot = new AttendanceSnapshot { Id = 1, CapturedAtUtc = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc) };
        foreach (var e in entries) snapshot.Entries.Add(e);
        return snapshot;
    }

    // "Aaron" added by hand still lands below "Zed" that the addon scanned — position encodes
    // provenance here, not the alphabet.
    [Fact]
    public void ManualEntries_SortBelowEveryScannedEntry()
    {
        var snapshot = SnapshotWith(
            Entry(1, "Zed"),
            Entry(2, "Aaron", manual: true),
            Entry(3, "Miex"));

        var rows = AttendanceSectionsBuilder.MapSnapshot(snapshot, Utc).Entries;

        Assert.Equal(new[] { "Miex", "Zed", "Aaron" }, rows.Select(r => r.CharacterName));
    }

    // Among themselves, hand-added people keep the order they were entered (by Id) rather than
    // going alphabetical — a freshly typed name appears at the bottom, next to the input.
    [Fact]
    public void ManualEntries_KeepInsertionOrderAmongThemselves()
    {
        var snapshot = SnapshotWith(
            Entry(1, "Solomag"),
            Entry(2, "Zeta", manual: true),
            Entry(3, "Alpha", manual: true),
            Entry(4, "Mid", manual: true));

        var rows = AttendanceSectionsBuilder.MapSnapshot(snapshot, Utc).Entries;

        Assert.Equal(new[] { "Solomag", "Zeta", "Alpha", "Mid" }, rows.Select(r => r.CharacterName));
    }

    // The scanned block is untouched by the feature: still plain alphabetical, case-insensitively.
    [Fact]
    public void ScannedEntries_StayAlphabetical()
    {
        var snapshot = SnapshotWith(
            Entry(1, "wrexsi"),
            Entry(2, "Agile"),
            Entry(3, "Millhouse"));

        var rows = AttendanceSectionsBuilder.MapSnapshot(snapshot, Utc).Entries;

        Assert.Equal(new[] { "Agile", "Millhouse", "wrexsi" }, rows.Select(r => r.CharacterName));
    }

    // The flag has to reach the view model, since that's what drives the row tint.
    [Fact]
    public void AddedManually_IsCarriedOntoTheRow()
    {
        var snapshot = SnapshotWith(Entry(1, "Agile"), Entry(2, "Walkin", manual: true));

        var rows = AttendanceSectionsBuilder.MapSnapshot(snapshot, Utc).Entries;

        Assert.False(rows.Single(r => r.CharacterName == "Agile").AddedManually);
        Assert.True(rows.Single(r => r.CharacterName == "Walkin").AddedManually);
    }

    // Rows written before the column existed default to false, so an all-legacy snapshot renders
    // exactly as it always did.
    [Fact]
    public void LegacyEntries_AllReadAsScanned()
    {
        var snapshot = SnapshotWith(Entry(1, "Rhyen"), Entry(2, "Bohemond"));

        var rows = AttendanceSectionsBuilder.MapSnapshot(snapshot, Utc).Entries;

        Assert.All(rows, r => Assert.False(r.AddedManually));
        Assert.Equal(new[] { "Bohemond", "Rhyen" }, rows.Select(r => r.CharacterName));
    }
}
