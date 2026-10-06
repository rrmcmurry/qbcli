using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using QbCli.Core;

namespace QbCli.QuickBooks;

public static class SdkXmlBuilder
{
    public static XElement Request(string operationName, JsonObject fields, string requestId, string version = "13.0", string country = "US")
    {
        var operation = SdkReference.Get(operationName);
        decimal protocol = ValidateProtocol(version,country);
        if (!operation.Versions.TryGetValue(country, out var minimum) || !decimal.TryParse(minimum, CultureInfo.InvariantCulture, out var min) || min <= 0 || min > protocol)
            throw Usage("Operation is not documented for the selected country/protocol.");
        var result = new XElement(operation.Request.Name, new XAttribute("requestID", requestId));
        Fill(operation.Request, fields, result, operationName, protocol, country, isRoot: true);
        return result;
    }
    public static decimal ValidateProtocol(string version,string country)
    {
        if (!decimal.TryParse(version, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var protocol) || protocol < 1 || protocol > decimal.Parse(SdkReference.MaximumProtocol, CultureInfo.InvariantCulture) || !Regex.IsMatch(version, @"^\d+\.\d+$"))
            throw Usage("Protocol must be a dotted version within the imported reference (1.0.." + SdkReference.MaximumProtocol + ").");
        if (country is not ("US" or "CA" or "UK" or "AU")) throw Usage("Country must be US, CA, UK or AU.");
        return protocol;
    }
    private static void Fill(SdkNode schema, JsonObject input, XElement output, string path, decimal version, string country, bool isRoot = false)
    {
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        if (input.TryGetPropertyValue("$attributes", out var attributes))
        {
            if (attributes is not JsonObject obj) throw Usage(path + ".$attributes must be an object.");
            var attributesByName = schema.Attributes.ToDictionary(a => a.Name, StringComparer.Ordinal);
            foreach (var attribute in obj)
            {
                if (attribute.Key == "requestID") throw Usage("requestID is managed by the batch engine.");
                if (!attributesByName.TryGetValue(attribute.Key, out var definition)) throw Usage(path + ": unknown attribute " + attribute.Key);
                Available(definition, version, country, path + "." + attribute.Key);
                output.SetAttributeValue(attribute.Key, Scalar(definition, attribute.Value, path + "." + attribute.Key, country, version));
            }
            consumed.Add("$attributes");
        }
        foreach (var attr in schema.Attributes.Where(a => a.Required && a.Name != "requestID"))
            if (output.Attribute(attr.Name) is null) throw Usage(path + ": missing required attribute " + attr.Name);
        foreach (var child in schema.Children) Emit(child, input, output, consumed, path, version, country);
        foreach (var field in input)
            if (!consumed.Contains(field.Key)) throw Usage(path + ": unknown or misplaced field " + field.Key);
        if (!isRoot && schema.Children.Count == 0 && input.Count != 0) throw Usage(path + ": this aggregate has no documented fields.");
    }
    private static IEnumerable<string> VisibleNames(SdkNode node)
    {
        if (node.Type == "skipwithchildren") yield break;
        if (node.Type is "skip" or "OR")
        {
            foreach (var child in node.Children) foreach (var name in VisibleNames(child)) yield return name;
        }
        else yield return node.Name;
    }
    private static bool Selected(SdkNode node, JsonObject input) => VisibleNames(node).Any(input.ContainsKey);
    private static void Emit(SdkNode node, JsonObject input, XElement output, HashSet<string> consumed, string path, decimal version, string country, bool repeatedChoice = false)
    {
        if (node.Type == "skipwithchildren") return;
        if (node.Type == "OR")
        {
            var choices = node.Children.Where(n => Selected(n, input)).ToArray();
            bool repeating = node.Count.Contains('n');
            if (repeating && input.TryGetPropertyValue("$choices", out var ordered))
            {
                if (!consumed.Add("$choices")) throw Usage(path + ": ordered choices are ambiguous at this schema location.");
                if (choices.Length != 0) throw Usage(path + ": do not mix $choices with fields from the same repeating choice.");
                if (ordered is not JsonArray entries) throw Usage(path + ".$choices must be an array of objects.");
                if (node.Required && entries.Count == 0) throw Usage(path + ": a required repeating choice cannot be empty.");
                Available(node, version, country, path);
                foreach (var entry in entries)
                {
                    if (entry is not JsonObject fields) throw Usage(path + ".$choices entries must be objects.");
                    var selected = node.Children.Where(n => Selected(n, fields)).ToArray();
                    if (selected.Length != 1) throw Usage(path + ".$choices entries must select exactly one documented alternative.");
                    var used = new HashSet<string>(StringComparer.Ordinal);
                    int beforeChoice = output.Elements().Count();
                    Emit(selected[0], fields, output, used, path + ".$choices", version, country);
                    if (fields.Any(f => !used.Contains(f.Key)) || output.Elements().Count() == beforeChoice)
                        throw Usage(path + ".$choices entry contains unknown fields or an empty alternative.");
                }
                return;
            }
            if (choices.Length > 1 && !repeating) throw Usage(path + ": mutually exclusive field groups were supplied.");
            if (choices.Length == 0) { if (node.Required) throw Usage(path + ": a required field choice is missing."); return; }
            Available(node, version, country, path);
            int before = output.Elements().Count();
            foreach (var choice in choices) Emit(choice, input, output, consumed, path, version, country, repeatedChoice: repeating);
            if (node.Required && output.Elements().Count() == before) throw Usage(path + ": a required repeating choice cannot be empty.");
            return;
        }
        if (node.Type == "skip")
        {
            if (!Selected(node, input) && !node.Required) return;
            Available(node, version, country, path);
            foreach (var child in node.Children) Emit(child, input, output, consumed, path, version, country);
            return;
        }
        if (!input.TryGetPropertyValue(node.Name, out var value))
        {
            if (node.Required) throw Usage(path + ": missing required field " + node.Name);
            return;
        }
        if (!consumed.Add(node.Name)) throw Usage(path + ": ambiguous schema field " + node.Name);
        Available(node, version, country, path + "." + node.Name);
        var max = repeatedChoice || node.Count.Contains('n') ? int.MaxValue : int.Parse(node.Count.Split('-')[^1].Trim(), CultureInfo.InvariantCulture);
        var values = value is JsonArray array ? array.ToArray() : new[] { value };
        if (values.Length > max || values.Length == 0 && node.Required) throw Usage(path + "." + node.Name + ": invalid repetition count.");
        foreach (var item in values)
        {
            var element = new XElement(node.Name);
            if (node.Type == "Aggregate")
            {
                if (item is not JsonObject obj) throw Usage(path + "." + node.Name + " must be an object.");
                Fill(node, obj, element, path + "." + node.Name, version, country);
            }
            else
            {
                // Scalar macro attributes (useMacro/defMacro) are represented as {$value:..., $attributes:{...}}.
                var scalar = item;
                if (item is JsonObject obj)
                {
                    if (!obj.ContainsKey("$value") || obj.Any(p => p.Key is not ("$value" or "$attributes"))) throw Usage(path + "." + node.Name + ": invalid scalar object.");
                    scalar = obj["$value"];
                    var attributesOnly = new JsonObject();
                    if (obj.ContainsKey("$attributes")) attributesOnly["$attributes"] = obj["$attributes"]?.DeepClone();
                    Fill(node, attributesOnly, element, path + "." + node.Name, version, country, isRoot: true);
                }
                element.Value = Scalar(node, scalar, path + "." + node.Name, country, version);
            }
            output.Add(element);
        }
    }
    private static void Available(SdkNode node, decimal version, string country, string path)
    {
        if (node.Supports.TryGetValue(country, out var supports) && !supports) throw Usage(path + " is not documented for this country.");
        if (node.Versions.TryGetValue(country, out var minimum) && decimal.TryParse(minimum, CultureInfo.InvariantCulture, out var min) && (min <= 0 || min > version))
            throw Usage(path + " requires another protocol version.");
    }
    private static string Scalar(SdkNode schema, JsonNode? value, string path, string country, decimal version)
    {
        if (value is not JsonValue scalar) throw Usage(path + " requires a scalar value; null is not a field-clearing instruction.");
        string text = scalar.TryGetValue<string>(out var str) ? str : scalar.ToJsonString();
        try { XmlConvert.VerifyXmlChars(text); } catch (XmlException) { throw Usage(path + " contains invalid XML characters."); }
        var lengthKey = country == "US" ? "DT" : country;
        if (schema.MaxLength.TryGetValue(lengthKey, out var limit) && int.TryParse(limit, out var maxLength) && text.Length > maxLength)
            throw Usage(path + " exceeds its documented maximum length.");
        bool valid = schema.Type switch
        {
            "STRTYPE" or "IDTYPE" => scalar.TryGetValue<string>(out _),
            "MACROTYPE" => scalar.TryGetValue<string>(out _) && SdkMacros.IsValidValue(text),
            "BOOLTYPE" => text is "true" or "false",
            "INTTYPE" => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "AMTTYPE" or "PRICETYPE" or "QUANTYPE" or "PERCENTTYPE" or "FLOATTYPE" => Regex.IsMatch(text, @"^[+-]?\d+(?:\.\d+)?$") && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
            "DATETYPE" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "DATETIMETYPE" => Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:[+-]\d{2}:\d{2}|Z)?$") && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            "GUIDTYPE" => text == "0" || Guid.TryParse(text, out _),
            "UUIDTYPE" => Guid.TryParse(text, out _),
            "TIMEINTERVALTYPE" => Regex.IsMatch(text, @"^-?P(?=.+)(?:\d+D)?(?:T(?=.+)(?:\d+H)?(?:\d+M)?(?:\d+(?:\.\d+)?S)?)?$"),
            "ENUMTYPE" => scalar.TryGetValue<string>(out _) && (schema.EnumValues.Count > 0 ? schema.EnumValues.Contains(text, StringComparer.Ordinal) : schema.Name switch
                { "iterator" => text is "Start" or "Continue" or "Stop", "metaData" => text is "NoMetaData" or "MetaDataOnly" or "MetaDataAndResponseData", _ => true }),
            _ => false
        };
        if (!valid) throw Usage(path + ": value does not match documented SDK type " + schema.Type);
        SdkNumericTypes.Validate(schema.Type,text,version,path);
        if (schema.Type == "ENUMTYPE" && schema.EnumValueVersions.TryGetValue(text, out var countries) && countries.TryGetValue(country, out var range))
        {
            if (!range.Supported || range.Minimum is { } first && version < decimal.Parse(first, CultureInfo.InvariantCulture)
                || range.MaximumExclusive is { } end && version >= decimal.Parse(end, CultureInfo.InvariantCulture)
                || range.IncludedRanges.Count>0 && !range.IncludedRanges.Any(interval=>
                    (interval.Minimum is null || version>=decimal.Parse(interval.Minimum,CultureInfo.InvariantCulture))
                    && (interval.MaximumExclusive is null || version<decimal.Parse(interval.MaximumExclusive,CultureInfo.InvariantCulture))))
                throw Usage(path + ": enum value is not documented for the selected country/protocol.");
        }
        if (schema.Minimum is { } min && decimal.TryParse(min, CultureInfo.InvariantCulture, out var minimum) && decimal.TryParse(text, CultureInfo.InvariantCulture, out var number) && number < minimum)
            throw Usage(path + ": value is below the documented minimum.");
        if (schema.Maximum is { } cap && decimal.TryParse(cap, CultureInfo.InvariantCulture, out var maximum) && decimal.TryParse(text, CultureInfo.InvariantCulture, out number) && number > maximum)
            throw Usage(path + ": value exceeds the documented maximum.");
        return text;
    }
    private static QbException Usage(string message) => new("invalid_arguments", message);
}
