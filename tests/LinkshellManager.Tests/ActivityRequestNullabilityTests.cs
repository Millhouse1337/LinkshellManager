using System.Linq;
using LinkshellManagerDiscordApp.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// Guards a footgun that silently broke every ToD save from the Discord Activity.
///
/// The Activity controllers are [ApiController]s and the project compiles with
/// &lt;Nullable&gt;enable&lt;/Nullable&gt;, so MVC gives every NON-nullable reference property on a
/// [FromBody] DTO an IMPLICIT [Required] (MvcOptions.SuppressImplicitRequiredAttributeFor-
/// NonNullableReferenceTypes is left at its default of false). A request that sends null for
/// such a property never reaches the action: MVC short-circuits with a ProblemDetails 400.
///
/// It bit the ToD form's old `lootDetails: null` (the field is gone now, along with ToD loot):
/// declared non-nullable, it was rejected before the action body ran, and because ProblemDetails
/// carries `errors` rather than the app's own `{ error }`, the Activity could only report
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

    // Every reference-typed field on the ToD requests is one the Activity may send as null.
    [Theory]
    [InlineData(typeof(ActivityCreateTodRequest))]
    [InlineData(typeof(ActivityUpdateTodRequest))]
    public void TodRequest_NoReferenceFieldIsImplicitlyRequired(System.Type requestType)
    {
        var required = BuildMetadataProvider().GetMetadataForType(requestType).Properties
            .Where(p => p.IsRequired && !p.ModelType.IsValueType)
            .Select(p => p.Name)
            .ToList();

        Assert.True(required.Count == 0,
            $"{requestType.Name} has implicitly [Required] fields ({string.Join(", ", required)}) -- "
                + "a null from the Activity would be rejected with a ProblemDetails 400 before the "
                + "action runs. Declare them nullable.");
    }
}
