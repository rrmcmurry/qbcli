using System.Text.Json;
using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed class SdkNode
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Required { get; set; }
    public string Count { get; set; } = "1";
    public Dictionary<string, bool> Supports { get; set; } = [];
    public Dictionary<string, string> Versions { get; set; } = [];
    public Dictionary<string, string> MaxLength { get; set; } = [];
    public string? Minimum { get; set; }
    public string? Maximum { get; set; }
    public List<string> EnumValues { get; set; } = [];
    public string? EnumSourceUrl { get; set; }
    public Dictionary<string, Dictionary<string, SdkEnumVersionRange>> EnumValueVersions { get; set; } = [];
    public List<string> ExcludedPrivateEnumValues { get; set; } = [];
    public List<SdkNode> Children { get; set; } = [];
    public List<SdkNode> Attributes { get; set; } = [];
}
public sealed class SdkEnumVersionRange
{
    public bool Supported { get; set; } = true;
    public string? Minimum { get; set; }
    public string? MaximumExclusive { get; set; }
    public List<SdkProtocolInterval> IncludedRanges { get; set; } = [];
}
public sealed class SdkProtocolInterval
{
    public string? Minimum { get; set; }
    public string? MaximumExclusive { get; set; }
}
public sealed class SdkOperation
{
    public List<SdkBehaviorNote> BehaviorNotes { get; set; } = [];
    public string Transport { get; set; } = "company";
    public string Name { get; set; } = "";
    public Dictionary<string, string> Versions { get; set; } = [];
    public SdkNode Request { get; set; } = new();
    public SdkNode Response { get; set; } = new();
    public bool RequiresApply => !Name.EndsWith("Query", StringComparison.Ordinal);
    public bool MutatesRecoveryState => Name == "DataEventRecoveryInfoDel";
    public bool AffectsUserInterface => Name is "ListDisplayAdd" or "ListDisplayMod" or "TxnDisplayAdd" or "TxnDisplayMod";
    public bool MutatesSubscriptions => Transport == "subscription" && RequiresApply;
    public bool MutatesCompany => Transport == "company" && RequiresApply && !MutatesRecoveryState && !AffectsUserInterface;
    public bool DesktopAvailable => new[] { "US", "CA", "UK", "AU" }.Any(c => Versions.TryGetValue(c, out var value) && decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var minimum) && minimum > 0);
}
public sealed class SdkBehaviorNote
{
    public string Id { get; set; } = "";
    public List<string> Operations { get; set; } = [];
    public string Summary { get; set; } = "";
    public string EvidenceScope { get; set; } = "";
}
public static class SdkReference
{
    public const string MaximumProtocol = "17.0";
    private static readonly string Source = ReadResource();
    private static readonly IReadOnlyDictionary<string, SdkOperation> operations = Load();
    private static string ReadResource(string suffix = "sdk-reference.json")
    {
        var assembly = typeof(SdkReference).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static IReadOnlyDictionary<string, SdkOperation> Load()
    {
        var node = JsonNode.Parse(Source)!;
        var list = node["operations"]!.Deserialize<List<SdkOperation>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var notes = JsonSerializer.Deserialize<List<SdkBehaviorNote>>(ReadResource("sdk-behavior-notes.json"),new JsonSerializerOptions {PropertyNameCaseInsensitive=true})!;
        if (notes.Any(n=>n.Operations.Any(name=>!list.Any(o=>o.Name==name)))) throw new InvalidOperationException("Unknown operation in behavioral evidence.");
        foreach(var operation in list)operation.BehaviorNotes=notes.Where(n=>n.Operations.Contains(operation.Name,StringComparer.Ordinal)).ToList();
        return list.ToDictionary(o => o.Name, StringComparer.Ordinal);
    }
    public static IReadOnlyDictionary<string, SdkOperation> Operations => operations;
    public static SdkOperation Get(string name) => operations.TryGetValue(name, out var operation) ? operation
        : throw new QbException("invalid_arguments", "Operation has no imported SDK schema. Run capabilities for supported operation names.");
    public static JsonNode Discover()
    {
        // Intern repeated structural facts so the comprehensive dump stays useful
        // to tools and agents instead of duplicating the same address/ref fields.
        var result = JsonNode.Parse(Source)!.AsObject();
        var definitions = new JsonObject();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(JsonObject node)
        {
            foreach (var key in new[] { "children", "attributes" })
                if (node[key] is JsonArray children)
                {
                    var references = children.Select(c => Intern((JsonObject)c!.DeepClone())).ToArray();
                    node[key] = new JsonArray(references.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
                }
            string canonical = node.ToJsonString();
            if (seen.TryGetValue(canonical, out var existing)) return existing;
            string id = "n" + (seen.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            seen.Add(canonical, id); definitions[id] = node;
            return id;
        }
        foreach (var operation in result["operations"]!.AsArray().OfType<JsonObject>())
        {
            var definition = Get(operation["name"]!.GetValue<string>());
            operation["requiresApply"] = definition.RequiresApply;
            operation["mutatesCompany"] = definition.MutatesCompany;
            operation["mutatesRecoveryState"] = definition.MutatesRecoveryState;
            operation["mutatesSubscriptions"] = definition.MutatesSubscriptions;
            operation["affectsUserInterface"] = definition.AffectsUserInterface;
            var notes = definition.BehaviorNotes;
            if(notes.Count>0)operation["behaviorNotes"]=JsonSerializer.SerializeToNode(notes,new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
            operation["requestRef"] = Intern((JsonObject)operation["request"]!.DeepClone()); operation.Remove("request");
            operation["responseRef"] = Intern((JsonObject)operation["response"]!.DeepClone()); operation.Remove("response");
        }
        result["schemaNodes"] = definitions;
        result["schemaNotation"] = "requestRef/responseRef identify entries in schemaNodes. children/attributes are ordered arrays of schema-node IDs. Follow references to reconstruct every field; sdk schema OPERATION expands one operation. IDs are local to this dump, not persistent SDK identifiers.";
        return result;
    }
}
