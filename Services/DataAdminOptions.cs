using System.Reflection;

namespace LinkshellManagerDiscordApp.Services;

// Turns the repo's controlled vocabularies into dropdown option lists. The repo keeps these as
// static classes of string consts, never enums (Models/LedgerAccountClasses.cs explains why), and
// only some of them carry an `All` list -- so the policy reads the consts by reflection where no
// list exists, which also means a new const shows up in the dropdown without a second edit.
public static class DataAdminOptions
{
    // Every public `const string` on the class, in declaration order.
    public static IReadOnlyList<string> FromConsts(Type constClass)
    {
        var values = constClass
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .OrderBy(field => field.MetadataToken)
            .Select(field => field.GetRawConstantValue() as string)
            .OfType<string>()
            .ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException($"Data Admin: {constClass.Name} declares no public const string values.");
        }
        return values;
    }
}
