using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

// Pins every controller endpoint the app serves: its HTTP verb, full route (with constraints), each
// parameter's name, type and where it is read from (path, query, body, services...), and the
// attributes and filters that guard it (auth, size limits, rate limits, antiforgery...).
//
// Why: the Activity API was one huge controller that is being split into smaller ones. That move
// must not change a single URL or rule. Controller and action class names are left out on purpose,
// so moving an action to another controller keeps this list identical, while changing anything a
// caller can see does not.
//
// When a change to the API is MEANT (a new endpoint, a renamed route), regenerate the file:
//   PowerShell:  $env:UPDATE_ROUTE_SNAPSHOT=1; dotnet test tests/LinkshellManager.Tests --filter ApiRouteSnapshotTests; Remove-Item Env:UPDATE_ROUTE_SNAPSHOT
//   bash:        UPDATE_ROUTE_SNAPSHOT=1 dotnet test tests/LinkshellManager.Tests --filter ApiRouteSnapshotTests
// and review the diff of Snapshots/api-routes.txt in the commit.
public class ApiRouteSnapshotTests
{
    private const string SnapshotFile = "api-routes.txt";
    private const string UpdateVariable = "UPDATE_ROUTE_SNAPSHOT";

    [Fact]
    public void EveryControllerEndpointMatchesTheSnapshot()
    {
        var actual = BuildRouteList();
        var path = Path.Combine(FindSnapshotsFolder(), SnapshotFile);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, string.Join("\n", actual) + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"No route snapshot at {path}. Run the test once with {UpdateVariable}=1 to write it.");
        var expected = File.ReadAllText(path)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();

        if (expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            return;
        }

        var removed = expected.Except(actual, StringComparer.Ordinal).Select(line => "- " + line);
        var added = actual.Except(expected, StringComparer.Ordinal).Select(line => "+ " + line);
        Assert.Fail(
            "The app's endpoints no longer match tests/LinkshellManager.Tests/Snapshots/" + SnapshotFile + ".\n"
            + "(- = in the snapshot but no longer served, + = served but not in the snapshot)\n"
            + string.Join("\n", removed.Concat(added)) + "\n\n"
            + $"If this change is meant, regenerate the file: set {UpdateVariable}=1, run this test again, "
            + "and commit the new snapshot with the change.");
    }

    // Two actions on the same verb + route make MVC throw "AmbiguousMatchException" on the first
    // request, not at startup - so splitting the controller must never leave a copy behind.
    [Fact]
    public void NoTwoActionsShareAVerbAndRoute()
    {
        var duplicates = ControllerActions()
            .Where(action => action.AttributeRouteInfo?.Template is not null)
            .SelectMany(action => Verbs(action).Select(verb => $"{verb} {action.AttributeRouteInfo!.Template!.ToLowerInvariant()}"))
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({group.Count()} actions)")
            .ToList();

        Assert.True(duplicates.Count == 0, "More than one action answers:\n" + string.Join("\n", duplicates));
    }

    [Fact]
    public void TheActivityApiIsInTheList()
    {
        // So an empty or broken list cannot pass by matching an empty snapshot.
        var routes = BuildRouteList();
        Assert.Contains(routes, line => line.StartsWith("GET api/activity/antiforgery ", StringComparison.Ordinal));
        Assert.True(routes.Count(line => line.Contains(" api/activity/", StringComparison.Ordinal)) > 100);
    }

    // One line per (verb, action), sorted. Nothing in a line names the controller or action class.
    internal static List<string> BuildRouteList()
    {
        return ControllerActions()
            .SelectMany(action => Verbs(action).Select(verb => Describe(verb, action)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static List<ControllerActionDescriptor> ControllerActions()
    {
        using var app = RouteTableTests.BuildApp();
        return app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToList();
    }

    private static IEnumerable<string> Verbs(ActionDescriptor action)
    {
        var verbs = (action.ActionConstraints ?? new List<IActionConstraintMetadata>())
            .OfType<HttpMethodActionConstraint>()
            .SelectMany(constraint => constraint.HttpMethods)
            .Select(verb => verb.ToUpperInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        return verbs.Count == 0 ? new[] { "ANY" } : verbs;
    }

    private static string Describe(string verb, ControllerActionDescriptor action)
    {
        var route = action.AttributeRouteInfo is { } info
            ? info.Template
                + (info.Order != 0 ? $" order={info.Order}" : string.Empty)
                + (info.Name is not null ? $" name={info.Name}" : string.Empty)
            // A conventional route: the URL IS the controller/action name, so it is the route here.
            : "(conventional) " + string.Join(",", action.RouteValues
                .Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        var parameters = string.Join(", ", action.Parameters.Select(parameter =>
        {
            var source = parameter.BindingInfo?.BindingSource?.Id ?? "?";
            var name = parameter.BindingInfo?.BinderModelName is { } modelName && modelName != parameter.Name
                ? $" as {modelName}"
                : string.Empty;
            return $"{parameter.Name}:{TypeName(parameter.ParameterType)}@{source}{name}";
        }));

        // Attribute filters (auth, antiforgery, size limits...) and the ones [ApiController] adds.
        // Global filters are left out: they apply to every action alike.
        var filters = string.Join(", ", action.FilterDescriptors
            .Where(filter => filter.Scope != FilterScope.Global)
            .Select(filter => $"{Describe(filter.Filter)}@{ScopeName(filter.Scope)}"));

        // Everything else MVC and the middleware read off an endpoint: [Authorize], [AllowAnonymous],
        // rate limiting, [ApiController], response types... Route, verb and filter attributes are
        // already above, and compiler-made attributes (nullability, async) say nothing about the API.
        var metadata = string.Join(", ", action.EndpointMetadata
            .Where(IsApiMetadata)
            .Select(Describe));

        return $"{verb} {route} | params: {parameters} | filters: {filters} | metadata: {metadata}";
    }

    private static bool IsApiMetadata(object item)
    {
        if (item is IFilterMetadata or IRouteTemplateProvider or IActionHttpMethodProvider)
        {
            return false;
        }

        var ns = item.GetType().Namespace ?? string.Empty;
        return ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || ns.StartsWith("LinkshellManagerDiscordApp", StringComparison.Ordinal);
    }

    private static string ScopeName(int scope) =>
        scope == FilterScope.Controller ? "controller"
        : scope == FilterScope.Action ? "action"
        : scope.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // The type name plus its simple public properties, e.g. EnableRateLimitingAttribute(PolicyName=addon-pair).
    private static string Describe(object item)
    {
        var type = item.GetType();
        var values = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.Name != "TypeId")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => (property.Name, Value: Format(SafeGet(property, item))))
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{pair.Name}={pair.Value}")
            .ToList();
        // [RequestSizeLimit(n)] keeps n behind an interface, not a public property.
        if (item is Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata sizeLimit)
        {
            values.Add($"MaxRequestBodySize={Format(sizeLimit.MaxRequestBodySize) ?? "none"}");
        }

        return values.Count == 0 ? TypeName(type) : $"{TypeName(type)}({string.Join(";", values)})";
    }

    private static object? SafeGet(PropertyInfo property, object item)
    {
        try
        {
            return property.GetValue(item);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    // Only plain values are written; anything else (delegates, services) is skipped.
    private static string? Format(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        Enum enumValue => enumValue.ToString(),
        Type type => TypeName(type),
        IFormattable number when value.GetType().IsPrimitive || value is decimal
            => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        IEnumerable items when value is not string => "[" + string.Join(",", items.Cast<object?>().Select(Format)) + "]",
        _ => null,
    };

    private static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return TypeName(inner) + "?";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(TypeName))}>";
    }

    private static string FindSnapshotsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "tests", "LinkshellManager.Tests");
            if (Directory.Exists(project))
            {
                var snapshots = Path.Combine(project, "Snapshots");
                Directory.CreateDirectory(snapshots);
                return snapshots;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate tests/LinkshellManager.Tests above {AppContext.BaseDirectory}.");
    }
}
