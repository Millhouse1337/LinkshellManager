using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

// Every service a controller asks for (constructor parameters and [FromServices] action
// parameters) must be registered, because MVC builds a controller only when a request for it
// arrives: a missing registration builds and starts fine, then answers that endpoint with a 500.
//
// Why now: the Activity API is being split into smaller controllers, each with its own
// constructor. A slip such as asking for a plain ILogger (only ILogger<T> is registered) or a
// service nobody registered would only show up in production.
//
// The real container cannot be built here: Program.cs needs a connection string and migrates the
// database on startup. So a type counts as registered when either
// - the framework part of the app provides it (the same host RouteTableTests.BuildApp builds:
//   logging, hosting, MVC, Identity, plus Program.cs's AddHttpClient and AddMemoryCache), or
// - Program.cs registers it by name (AddScoped<T>, AddSingleton<T>, AddTransient<T>,
//   AddDbContext<T>, or an AddSingleton(sp => ... return new T(...)) factory).
public class ControllerServicesRegisteredTests
{
    [Fact]
    public void EveryServiceAControllerAsksForIsRegistered()
    {
        using var app = RouteTableTests.BuildApp();
        var frameworkServices = app.Services.GetRequiredService<IServiceProviderIsService>();
        var programRegistrations = ProgramRegistrations();
        var appAssembly = typeof(Program).Assembly;

        var feature = new ControllerFeature();
        app.Services.GetRequiredService<ApplicationPartManager>().PopulateFeature(feature);
        var controllers = feature.Controllers
            .Select(info => info.AsType())
            .Where(type => type.Assembly == appAssembly)
            .ToList();
        // So an empty list cannot pass.
        Assert.Contains(controllers, type => type.Name.StartsWith("Activity", StringComparison.Ordinal));
        Assert.Contains(controllers, type => type.Name == "AddonApiController");

        var asked = new List<(string Where, Type Service)>();
        foreach (var controller in controllers)
        {
            // MVC's default activator picks the public constructor; the app has one per controller.
            var constructors = controller.GetConstructors();
            Assert.True(constructors.Length == 1, $"{controller.Name} has {constructors.Length} public constructors.");
            asked.AddRange(constructors[0].GetParameters()
                .Select(parameter => ($"{controller.Name}({parameter.Name})", parameter.ParameterType)));
        }

        asked.AddRange(app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly == appAssembly)
            .SelectMany(action => action.Parameters
                .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Services)
                .Select(parameter => ($"{action.ControllerName}.{action.ActionName}({parameter.Name})", parameter.ParameterType))));

        // The framework services Program.cs adds on top of what BuildApp has.
        using var extraProvider = new ServiceCollection()
            .AddHttpClient()
            .AddMemoryCache()
            .BuildServiceProvider();
        var extraServices = extraProvider.GetRequiredService<IServiceProviderIsService>();

        var missing = asked
            .Where(item => !frameworkServices.IsService(item.Service)
                && !extraServices.IsService(item.Service)
                && !programRegistrations.Contains(item.Service.Name))
            .Select(item => $"{item.Where}: {item.Service.FullName}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These controller services are not registered (add them to Program.cs, or ask for a registered type such as ILogger<T>):\n"
            + string.Join("\n", missing));
    }

    // A plain ILogger is the easy slip when a controller hands its logger to a shared base class.
    [Fact]
    public void APlainILoggerIsNotRegistered()
    {
        using var app = RouteTableTests.BuildApp();
        var services = app.Services.GetRequiredService<IServiceProviderIsService>();

        Assert.False(services.IsService(typeof(Microsoft.Extensions.Logging.ILogger)));
        Assert.True(services.IsService(typeof(Microsoft.Extensions.Logging.ILogger<ControllerServicesRegisteredTests>)));
        Assert.DoesNotContain("ILogger", ProgramRegistrations());
    }

    private static HashSet<string> ProgramRegistrations()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "Program.cs"));
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(text, @"\.Add(?:Scoped|Singleton|Transient|DbContext)<\s*([\w.]+)"))
        {
            names.Add(match.Groups[1].Value.Split('.').Last());
        }

        // AddSingleton(sp => { ... return new DataAdminCatalog(...); })
        foreach (Match match in Regex.Matches(text, @"\.AddSingleton\(\s*sp\s*=>[\s\S]*?return new ([\w.]+)\("))
        {
            names.Add(match.Groups[1].Value.Split('.').Last());
        }

        // So a broken pattern cannot pass by finding nothing.
        Assert.Contains("ApplicationDbContext", names);
        Assert.Contains("LinkshellPermissions", names);
        Assert.Contains("DataAdminCatalog", names);
        return names;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Program.cs"))
                && Directory.Exists(Path.Combine(directory.FullName, "Controllers")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the repo root above {AppContext.BaseDirectory}.");
    }
}
