using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// A partial referenced by BARE NAME is resolved against the executing CONTROLLER's view folder
/// (/Views/&lt;controller&gt;/ then /Views/Shared/) -- never against the folder the referencing
/// partial lives in.
///
/// So the moment a partial is mounted from another controller's page, every bare-name reference
/// inside it starts searching the wrong folder and throws "The partial view '_X' was not found" at
/// RUNTIME. Nothing catches it earlier: Razor compiles fine, and the partial keeps working on its
/// own page, so the failure only shows up on the borrowing page -- and only once the data reaches
/// the branch that renders it.
///
/// That is exactly how /Event started 500ing: Views/Event/_CurrentFieldActivity mounts
/// WindowEvents/_AttendanceSections, which asked for "_WindowEventCard" by name. It rendered for
/// months from WindowEvents/History, and broke on the Event page for every linkshell that had an
/// open attendance event.
///
/// This pins the rule for every folder that is mounted cross-controller.
/// </summary>
public class CrossControllerPartialTests
{
    // @await Html.PartialAsync("X" / @Html.Partial("X" / <partial name="X"
    private static readonly Regex PartialReference = new(
        """(?:PartialAsync\(\s*"(?<name>[^"]+)"|Html\.Partial\(\s*"(?<name>[^"]+)"|<partial\s+name\s*=\s*"(?<name>[^"]+)")""",
        RegexOptions.Compiled);

    /// <summary>
    /// Every view that another folder mounts by path must address its own nested partials by path
    /// too -- and so must anything it pulls in, transitively, since those render under the foreign
    /// controller as well.
    ///
    /// Per FILE, not per folder: a full page like PartySetup/Create.cshtml is only ever rendered by
    /// its own controller, so its bare names resolve correctly and are not a finding. It is the
    /// borrowed PARTIALS beside it that have to be path-addressed.
    /// </summary>
    [Fact]
    public void PartialsMountedCrossControllerReferenceTheirOwnPartialsByPath()
    {
        var viewsRoot = FindRepoDirectory("Views");
        var views = Directory.GetFiles(viewsRoot, "*.cshtml", SearchOption.AllDirectories);
        var textByFile = views.ToDictionary(f => f, File.ReadAllText, StringComparer.OrdinalIgnoreCase);

        // Seed: every view pulled in by path from a view in a DIFFERENT folder
        // ("../WindowEvents/_X" or "~/Views/WindowEvents/_X.cshtml").
        var borrowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, text) in textByFile)
        {
            foreach (var name in PathReferencesIn(text))
            {
                var target = ResolvePathReference(file, name, viewsRoot);
                if (target is null) continue;
                if (!string.Equals(
                        Path.GetDirectoryName(target), Path.GetDirectoryName(file), StringComparison.OrdinalIgnoreCase))
                {
                    borrowed.Add(target);
                }
            }
        }

        // The mount that started this. If it ever stops being cross-controller the sweep below would
        // silently cover nothing, so pin that the set is really populated.
        Assert.Contains(
            borrowed,
            path => Path.GetFileName(path).Equals("_WindowEventCard.cshtml", StringComparison.OrdinalIgnoreCase));

        // Transitive: anything a borrowed view pulls in renders under the foreign controller too.
        var queue = new Queue<string>(borrowed);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!textByFile.TryGetValue(current, out var text)) continue;
            foreach (var name in PathReferencesIn(text))
            {
                var target = ResolvePathReference(current, name, viewsRoot);
                if (target is not null && borrowed.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        var offenders = new List<string>();
        foreach (var file in borrowed.Where(textByFile.ContainsKey))
        {
            var text = textByFile[file];
            var folder = Path.GetFileName(Path.GetDirectoryName(file))!;
            foreach (Match match in PartialReference.Matches(text))
            {
                var name = match.Groups["name"].Value;
                if (name.Contains('/')) continue;

                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add(
                    $"{folder}/{Path.GetFileName(file)}:{line} references \"{name}\" by bare name, but this "
                    + $"view is mounted from another controller's page -- use \"~/Views/{folder}/{name}.cshtml\".");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> PathReferencesIn(string text) =>
        PartialReference.Matches(text)
            .Select(match => match.Groups["name"].Value)
            .Where(name => name.Contains('/'));

    /// <summary>The full path a "~/Views/…" or "../Folder/…" partial reference points at, or null.</summary>
    private static string? ResolvePathReference(string referencingFile, string name, string viewsRoot)
    {
        var repoRoot = Path.GetDirectoryName(viewsRoot)!;
        var resolved = name.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(repoRoot, name[2..])
            : Path.Combine(Path.GetDirectoryName(referencingFile)!, name);
        if (!resolved.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
        {
            resolved += ".cshtml";
        }
        var full = Path.GetFullPath(resolved);
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Every partial addressed by path actually exists at that path -- the other half of the same
    /// failure, and equally invisible until the page runs. Case-sensitive: the droplet is Linux.
    /// </summary>
    [Fact]
    public void EveryPathReferencedPartialExists()
    {
        var viewsRoot = FindRepoDirectory("Views");
        var repoRoot = Path.GetDirectoryName(viewsRoot)!;
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(viewsRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in PartialReference.Matches(text))
            {
                var name = match.Groups["name"].Value;
                if (!name.Contains('/')) continue;

                var resolved = name.StartsWith("~/", StringComparison.Ordinal)
                    ? Path.Combine(repoRoot, name[2..])
                    : Path.Combine(Path.GetDirectoryName(file)!, name);
                if (!resolved.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                {
                    resolved += ".cshtml";
                }

                var full = Path.GetFullPath(resolved);
                if (!File.Exists(full))
                {
                    missing.Add($"{Path.GetFileName(file)} -> \"{name}\" (looked for {full})");
                    continue;
                }

                // File.Exists is case-insensitive on Windows and the server is not, so compare the
                // reference against the name as it is actually spelled on disk.
                var onDisk = Directory
                    .GetFiles(Path.GetDirectoryName(full)!, "*.cshtml")
                    .First(candidate => string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(Path.GetFileName(onDisk), Path.GetFileName(full), StringComparison.Ordinal))
                {
                    missing.Add(
                        $"{Path.GetFileName(file)} -> \"{name}\" differs in CASE from {Path.GetFileName(onDisk)}; "
                        + "this resolves on Windows and fails on the Linux droplet.");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    private static string FindRepoDirectory(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
