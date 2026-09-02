using System.Linq;
using LinkshellManagerDiscordApp.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// Guards a footgun that silently broke every ToD save from the Discord Activity.
///
/// ActivityDataController is an [ApiController] and the project compiles with
/// &lt;Nullable&gt;enable&lt;/Nullable&gt;, so MVC gives every NON-nullable reference property on a
/// [FromBody] DTO an IMPLICIT [Required] (MvcOptions.SuppressImplicitRequiredAttributeFor-
/// NonNullableReferenceTypes is left at its default of false). A request that sends null for
/// such a property never reaches the action: MVC short-circuits with a ProblemDetails 400.
///
/// The ToD form stopped carrying loot -- it now always sends `lootDetails: null`, and the update
/// action reads null as "leave the existing loot alone". With LootDetails declared non-nullable
/// that was rejected before the action body ran, and because ProblemDetails carries `errors`
/// rather than the app's own `{ error }`, the Activity could only report
/// "The server returned an unexpected response (status 400)".
///
/// So: any body field the client may legitimately send as null MUST be declared nullable.
/// </summary>
public class ActivityRequestNullabilityTests
{
    private static IModelMetadataProvider BuildMetadataProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        return services.BuildServiceProvider().GetRequiredService<IModelMetadataProvider>();
    }

    private static bool IsImplicitlyRequired(System.Type requestType, string propertyName)
    {
        var metadata = BuildMetadataProvider().GetMetadataForType(requestType);
        var property = metadata.Properties.Single(p => p.Name == propertyName);
        return property.IsRequired;
    }

    [Theory]
    [InlineData(typeof(ActivityCreateTodRequest))]
    [InlineData(typeof(ActivityUpdateTodRequest))]
    public void TodRequest_LootDetails_IsOptional(System.Type requestType)
    {
        Assert.False(
            IsImplicitlyRequired(requestType, nameof(ActivityCreateTodRequest.LootDetails)),
            $"{requestType.Name}.LootDetails is implicitly [Required] -- the Activity always sends "
                + "it as null, so every ToD save would be rejected with a ProblemDetails 400 before "
                + "the action runs. Declare it as IReadOnlyList<...>? to keep null bindable.");
    }
}
