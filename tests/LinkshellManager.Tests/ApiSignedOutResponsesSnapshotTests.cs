using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

// Calls EVERY attribute-routed endpoint of the real app three ways and pins what comes back:
//   1. signed out (no credentials at all),
//   2. with a Discord bearer that Discord turns down,
//   3. with that same bad bearer AND a valid website cookie.
// 2 and 3 are written as "same" when they match 1, which is the rule for everything but the
// addon (its own token scheme) and /api/me (it reports Discord's answer itself): a bad bearer is
// treated exactly like no credentials, and a cookie never rescues it.
//
// Why: each Activity action answers a signed-out caller with its own 401 message, and the
// Activity and website show that text. Moving sign-in into one place must not change one of them.
//
// Route values are filled with placeholders (ids = 1), POST/PUT/DELETE send "{}" as JSON. Success
// bodies are not pinned (only the status); every other body is, with trace/correlation ids blanked.
//
// When a change is MEANT, regenerate the file:
//   PowerShell:  $env:UPDATE_SIGNED_OUT_SNAPSHOT=1; dotnet test tests/LinkshellManager.Tests --filter ApiSignedOutResponsesSnapshotTests; Remove-Item Env:UPDATE_SIGNED_OUT_SNAPSHOT
//   bash:        UPDATE_SIGNED_OUT_SNAPSHOT=1 dotnet test tests/LinkshellManager.Tests --filter ApiSignedOutResponsesSnapshotTests
// and review the diff of Snapshots/api-signed-out-responses.txt in the commit.
public sealed class ApiSignedOutResponsesSnapshotTests : IDisposable
{
    private const string SnapshotFile = "api-signed-out-responses.txt";
    private const string UpdateVariable = "UPDATE_SIGNED_OUT_SNAPSHOT";
    private const string RejectedToken = "SweepRejectedTokenFFFFFFFFFFFFF";
    private const string CookieUserId = "sweep-cookie-user";

    private readonly ApiPipelineHost _app = new();

    [Fact]
    public async Task EveryEndpointAnswersSignedOutCallersAsInTheSnapshot()
    {
        await _app.SeedUserAsync(CookieUserId, "400000000000000001");
        var cookie = await _app.SignInCookieAsync(CookieUserId);

        var lines = new List<string>();
        using var client = _app.NewClient();
        foreach (var (verb, path, label) in Endpoints())
        {
            var signedOut = await DescribeAsync(client, verb, path, bearer: null, cookie: null);
            var badBearer = await DescribeAsync(client, verb, path, RejectedToken, cookie: null);
            var badBearerWithCookie = await DescribeAsync(client, verb, path, RejectedToken, cookie);
            lines.Add($"{label} | signed out: {signedOut}"
                + $" | bad bearer: {(badBearer == signedOut ? "same" : badBearer)}"
                + $" | bad bearer + cookie: {(badBearerWithCookie == signedOut ? "same" : badBearerWithCookie)}");
        }

        // So an empty or broken sweep cannot pass by matching an empty snapshot.
        Assert.True(lines.Count(line => line.Contains(" api/activity/", StringComparison.Ordinal)) > 100);
        // Discord was asked about the rejected token once; the next ~800 requests used the
        // 60-second "rejected" memory. And nothing else left the process.
        Assert.Equal(1, _app.Discord.TokenLookups(RejectedToken));
        Assert.Empty(_app.Discord.UnexpectedCalls);

        var snapshotPath = Path.Combine(FindSnapshotsFolder(), SnapshotFile);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(snapshotPath, string.Join("\n", lines) + "\n");
            return;
        }

        Assert.True(File.Exists(snapshotPath), $"No snapshot at {snapshotPath}. Run once with {UpdateVariable}=1 to write it.");
        var expected = File.ReadAllText(snapshotPath)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();
        if (expected.SequenceEqual(lines, StringComparer.Ordinal))
        {
            return;
        }

        var removed = expected.Except(lines, StringComparer.Ordinal).Select(line => "- " + line);
        var added = lines.Except(expected, StringComparer.Ordinal).Select(line => "+ " + line);
        Assert.Fail(
            "Signed-out answers no longer match tests/LinkshellManager.Tests/Snapshots/" + SnapshotFile + ".\n"
            + "(- = in the snapshot, + = what the app answers now)\n"
            + string.Join("\n", removed.Concat(added)) + "\n\n"
            + $"If this change is meant, set {UpdateVariable}=1, run this test again, and commit the new snapshot.");
    }

    private static async Task<string> DescribeAsync(HttpClient client, string verb, string path, string? bearer, string? cookie)
    {
        var body = verb is "GET" or "HEAD" ? null : "{}";
        using var request = ApiPipelineRequests.Build(new HttpMethod(verb), path, bearer, cookie, guildId: null, body);
        using var response = await client.SendAsync(request);
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
        {
            return status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (status is >= 300 and < 400)
        {
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            var query = location.IndexOf('?', StringComparison.Ordinal);
            return $"{status} -> {(query >= 0 ? location[..query] : location)}";
        }

        var text = await response.Content.ReadAsStringAsync();
        if (text.TrimStart().StartsWith('<'))
        {
            text = "<html>";
        }

        text = Regex.Replace(text, "\"(traceId|correlationId)\":\"[^\"]*\"", "\"$1\":\"*\"");
        text = text.ReplaceLineEndings(" ");
        if (text.Length > 400)
        {
            text = text[..400] + "...";
        }

        return text.Length == 0 ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{status} {text}";
    }

    // (verb, request path, snapshot label) for every attribute-routed controller action, sorted.
    private IEnumerable<(string Verb, string Path, string Label)> Endpoints()
    {
        var actions = _app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.AttributeRouteInfo?.Template is not null);

        return actions
            .SelectMany(action =>
            {
                var template = action.AttributeRouteInfo!.Template!;
                var verbs = action.EndpointMetadata.OfType<HttpMethodMetadata>()
                    .SelectMany(metadata => metadata.HttpMethods)
                    .Distinct()
                    .DefaultIfEmpty("GET");
                return verbs.Select(verb => (Verb: verb.ToUpperInvariant(), Path: "/" + FillRoute(template).TrimStart('/'), Label: $"{verb.ToUpperInvariant()} {template}"));
            })
            .DistinctBy(item => item.Label)
            .OrderBy(item => item.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static string FillRoute(string template) =>
        Regex.Replace(template, @"\{\*{0,2}(\w+)(:[^}]*)?\??\}", match =>
        {
            var constraint = match.Groups[2].Value;
            if (constraint.StartsWith(":int", StringComparison.Ordinal) || constraint.StartsWith(":long", StringComparison.Ordinal))
            {
                return "1";
            }

            return match.Groups[1].Value switch
            {
                "board" => "sky",
                "fileName" => "missing.png",
                _ => "x",
            };
        });

    private static string FindSnapshotsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var snapshots = Path.Combine(directory.FullName, "tests", "LinkshellManager.Tests", "Snapshots");
            if (Directory.Exists(snapshots))
            {
                return snapshots;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate tests/LinkshellManager.Tests/Snapshots above {AppContext.BaseDirectory}.");
    }

    public void Dispose() => _app.Dispose();
}
