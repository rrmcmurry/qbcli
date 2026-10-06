using System.Text.Json;
using System.Text.Json.Nodes;
using QbCli.Core;
using QbCli.QuickBooks;

namespace QbCli;

public static class SdkInput
{
    public static JsonObject Read(string? path)
    {
        if (path is null) return new();
        try
        {
            var file = new FileInfo(path);
            if (file.Length > 64 * 1024 * 1024) throw Usage("Input exceeds the 64 MiB application limit.");
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            Unique(document.RootElement);
            return JsonNode.Parse(text) as JsonObject ?? throw Usage("Input must be a JSON object.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw Usage("Input file could not be read as strict JSON. Check its path, encoding and syntax."); }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Usage("Duplicate JSON property names are not accepted.");
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Unique(child);
    }
    public static SdkPlan Plan(Options options)
    {
        var input = Read(options.InputFile);
        if (options.Command is not ("sdk batch" or "sdk subscription-batch"))
            return new(options.Protocol, options.Country, "stopOnError", [new("1", options.Value!, input)], options.BatchSize);
        Allowed(input, "protocolVersion", "country", "onError", "requests", "messageSetAttributes", "maximumBatchBytes");
        if (input["requests"] is not JsonArray calls) throw Usage("Batch input requires a requests array.");
        var requests = new List<SdkCall>();
        foreach (var entry in calls)
        {
            if (entry is not JsonObject call) throw Usage("Each batch request must be an object.");
            Allowed(call, "requestId", "operation", "fields");
            if (call["fields"] is not JsonObject fields) throw Usage("Each request needs a fields object.");
            requests.Add(new(Text(call, "requestId") ?? (requests.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Text(call, "operation") ?? throw Usage("Each request needs an operation name."), (JsonObject)fields.DeepClone()));
        }
        Dictionary<string,string>? messageAttributes = null;
        if (input.ContainsKey("messageSetAttributes"))
        {
            if (input["messageSetAttributes"] is not JsonObject attributes) throw Usage("messageSetAttributes must be an object.");
            messageAttributes = attributes.ToDictionary(a => a.Key, a => Text(attributes,a.Key) ?? throw Usage("Message-set attributes cannot be null."), StringComparer.Ordinal);
        }
        return new(Text(input, "protocolVersion") ?? options.Protocol, Text(input, "country") ?? options.Country,
            Text(input, "onError") ?? "stopOnError", requests, options.BatchSize,
            Integer(input,"maximumBatchBytes",1_048_576,1024,16_777_216),messageAttributes);
    }
    private static int Integer(JsonObject input,string key,int fallback,int minimum,int maximum)
    {
        if(!input.ContainsKey(key))return fallback;
        if(input[key] is JsonValue value && value.TryGetValue<int>(out var number) && number>=minimum && number<=maximum)return number;
        throw Usage(key+" must be an integer within "+minimum+".."+maximum+".");
    }
    private static string? Text(JsonObject input, string key)
    {
        if (!input.ContainsKey(key)) return null;
        if (input[key] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw Usage(key + " must be a string.");
    }
    private static void Allowed(JsonObject input, params string[] names)
    {
        foreach (var property in input) if (!names.Contains(property.Key, StringComparer.Ordinal)) throw Usage("Unknown input property " + property.Key);
    }
    private static QbException Usage(string message) => new("invalid_arguments", message);
}
