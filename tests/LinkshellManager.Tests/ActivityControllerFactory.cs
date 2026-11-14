using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinkshellManager.TestSupport;

// Builds an Activity controller for a test from the few services that test needs, matched to the
// constructor BY TYPE. Anything not given is passed as null (the test never reaches it), and an
// ILogger<T> becomes a NullLogger<T>.
//
// Why: a test written as `new ActivityEventsController(db, null!, null!, ...)` breaks every time
// the constructor changes and is easy to get wrong by position; this keeps working, and moving a
// test to another controller is only a change of the type argument.
//
// Shared with LinkshellManager.PostgresTests, which compiles this same file (see its .csproj).
internal static class ActivityControllerFactory
{
    internal static T Create<T>(HttpContext http, params object[] services) where T : ControllerBase
    {
        var constructor = typeof(T).GetConstructors().Single();
        var arguments = constructor.GetParameters()
            .Select(parameter => Pick(typeof(T), parameter.ParameterType, services))
            .ToArray();

        var controller = (T)constructor.Invoke(arguments);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static object? Pick(Type controller, Type parameterType, object[] services)
    {
        var fits = services.Where(parameterType.IsInstanceOfType).ToList();
        if (fits.Count > 1)
        {
            throw new ArgumentException(
                $"{controller.Name}: more than one given service fits a {parameterType.Name} parameter.");
        }

        if (fits.Count == 1)
        {
            return fits[0];
        }

        if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            return Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(parameterType.GetGenericArguments()));
        }

        return null;
    }
}
