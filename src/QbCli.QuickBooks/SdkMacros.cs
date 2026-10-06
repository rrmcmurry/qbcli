using System.Text.RegularExpressions;
using System.Xml.Linq;
using QbCli.Core;

namespace QbCli.QuickBooks;

/// <summary>Macros belong to one company session, which one prepared plan owns.</summary>
public static class SdkMacros
{
    public static bool IsValidValue(string value) => Regex.IsMatch(value,
        @"\A[A-Za-z][A-Za-z0-9]*:[A-Za-z0-9_-]{1,40}\z", RegexOptions.CultureInvariant);

    public static void ValidatePlan(IReadOnlyList<XElement> requests)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            // The SDK documents use in a later request. Definitions in this
            // request become available only after its references are checked.
            var local = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in request.DescendantsAndSelf())
            {
                if (element.Attribute("defMacro") is { } definition &&
                    (defined.Contains(definition.Value) || !local.Add(definition.Value)))
                    throw Usage("Macro definitions must be unique within the plan's company session.");
                if (element.Attribute("useMacro") is { } reference && !defined.Contains(reference.Value))
                    throw Usage("Every useMacro must match a defMacro in an earlier request of the same plan. Macros cannot be imported from another CLI invocation.");
            }
            defined.UnionWith(local);
        }
    }

    private static QbException Usage(string message) => new("invalid_arguments", message);
}
