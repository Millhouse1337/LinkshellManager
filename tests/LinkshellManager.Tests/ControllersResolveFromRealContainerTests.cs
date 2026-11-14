using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LinkshellManager.Tests;

// Builds EVERY controller from the app's REAL service container, the one Program.cs builds, the
// same way MVC does when a request arrives. A controller that asks for a service nobody
// registered then fails here, not as a 500 in production.
//
// ControllerServicesRegisteredTests checks the same thing by reading Program.cs as text; this one
// runs Program.cs itself. It stops Program.cs right after builder.Build(), the trick
// WebApplicationFactory and "dotnet ef" use: the host announces itself on a DiagnosticListener,
// we keep it and throw. So no migration, seeding, background service or port ever runs, and the
// fake connection string is never opened (a DbContext only connects on its first query).
public class ControllersResolveFromRealContainerTests
{
    // Only the Program.cs this test starts is stopped, never a host another test builds at the same time.
    private static readonly AsyncLocal<bool> Capturing = new();

    [Fact]
    public void EveryControllerResolvesFromTheRealContainer()
    {
        using var host = BuildProgramHost();
        var services = host.Services;
        var appAssembly = typeof(Program).Assembly;

        var feature = new ControllerFeature();
        services.GetRequiredService<ApplicationPartManager>().PopulateFeature(feature);
        var controllers = feature.Controllers
            .Where(type => type.Assembly == appAssembly)
            .ToList();
        // So an empty list (for example the wrong application name) cannot pass.
        Assert.Contains(controllers, type => type.Name == "ActivityOverviewController");
        Assert.Contains(controllers, type => type.Name == "ActivitySettingsController");
        Assert.Contains(controllers, type => type.Name == "AddonApiController");

        var activator = services.GetRequiredService<IControllerActivator>();
        var failures = new List<string>();
        foreach (var controller in controllers)
        {
            using var scope = services.CreateScope();
            var context = new ControllerContext(new ActionContext(
                new DefaultHttpContext { RequestServices = scope.ServiceProvider },
                new RouteData(),
                new ControllerActionDescriptor { ControllerTypeInfo = controller }));
            try
            {
                var instance = activator.Create(context);
                activator.Release(context, instance);
            }
            catch (Exception ex)
            {
                failures.Add($"{controller.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // [FromServices] action parameters are resolved per request too.
        var fromServices = services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly == appAssembly)
            .SelectMany(action => action.Parameters
                .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Services)
                .Select(parameter => (Where: $"{action.ControllerName}.{action.ActionName}({parameter.Name})", parameter.ParameterType)));
        foreach (var (where, type) in fromServices)
        {
            using var scope = services.CreateScope();
            try
            {
                scope.ServiceProvider.GetRequiredService(type);
            }
            catch (Exception ex)
            {
                failures.Add($"{where}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "These controllers or services cannot be built from the app's container:\n"
            + string.Join("\n", failures.Distinct().Order(StringComparer.Ordinal)));
    }

    // The Discord bot's dispatcher finds its routes through IDiscordInteractionHandler. A feature
    // handler written but not registered that way builds and starts fine, and every button it owns
    // then answers "That action isn't recognized." So: every implementation is registered, once,
    // and the real container builds the dispatcher (whose constructor refuses overlapping ids).
    [Fact]
    public void EveryDiscordInteractionHandlerIsRegistered()
    {
        using var host = BuildProgramHost();
        using var scope = host.Services.CreateScope();

        var registered = scope.ServiceProvider
            .GetServices<LinkshellManagerDiscordApp.Controllers.Discord.IDiscordInteractionHandler>()
            .Select(handler => handler.GetType().Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        var written = typeof(Program).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(LinkshellManagerDiscordApp.Controllers.Discord.IDiscordInteractionHandler).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(written.Count >= 7, "Found too few Discord handlers; the reflection above is broken.");
        Assert.Equal(written, registered);
        Assert.NotEmpty(scope.ServiceProvider
            .GetRequiredService<LinkshellManagerDiscordApp.Controllers.Discord.DiscordInteractionDispatcher>().Routes);
    }

    // Runs Program.cs up to builder.Build() and hands back the host it built.
    private static IHost BuildProgramHost()
    {
        IHost? captured = null;
        using var subscription = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name != "Microsoft.Extensions.Hosting")
            {
                return;
            }

            listener.Subscribe(new Observer<KeyValuePair<string, object?>>(pair =>
            {
                if (pair.Key == "HostBuilt" && Capturing.Value && pair.Value is IHost host)
                {
                    captured = host;
                    throw new HostAbortedException();
                }
            }));
        }));

        // Production, as on the droplet (and so no user-secrets). Fake values for the settings
        // Program.cs refuses to start without; nothing here connects to them.
        string[] args =
        [
            "--environment=Production",
            $"--applicationName={typeof(Program).Assembly.GetName().Name}",
            "--ConnectionStrings:DefaultConnection=Host=127.0.0.1;Port=1;Database=never_opened;Username=none;Password=none",
            "--Discord:ClientId=1",
            "--Discord:ClientSecret=test-secret",
            "--Database:RunMigrationsOnStartup=false",
        ];

        Capturing.Value = true;
        try
        {
            typeof(Program).Assembly.EntryPoint!.Invoke(null, [args]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is HostAbortedException)
        {
        }
        finally
        {
            Capturing.Value = false;
        }

        return captured ?? throw new InvalidOperationException("Program.cs finished without building a host.");
    }

    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
