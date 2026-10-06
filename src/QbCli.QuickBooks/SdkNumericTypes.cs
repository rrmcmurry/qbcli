using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

/// <summary>Enforces explicitly documented numeric formats; unresolved types keep their existing validation.</summary>
public static class SdkNumericTypes
{
    public static void Validate(string type,string text,decimal protocol,string path)
    {
        var rule=type switch
        {
            "PRICETYPE"=>(Left:protocol>=8?10:8,Right:5,Total:protocol>=8?15:13,PointRequired:protocol<8),
            "PERCENTTYPE"=>(Left:protocol>=8?10:7,Right:5,Total:protocol>=8?15:12,PointRequired:false),
            "QUANTYPE"=>(Left:protocol>=8?10:8,Right:5,Total:protocol>=8?15:13,PointRequired:false),
            "FLOATTYPE"=>(Left:8,Right:6,Total:9,PointRequired:false),
            _=>(Left:0,Right:0,Total:0,PointRequired:false)
        };
        if(rule.Total==0)return;
        // Scalar validation already requires a signed, invariant decimal token.
        // Count transmitted digits, excluding only the sign and decimal point;
        // never silently round, remove zeros or rewrite the caller's number.
        var unsigned=text.TrimStart('+','-');int point=unsigned.IndexOf('.');
        int left=point<0?unsigned.Length:point;
        int right=point<0?0:unsigned.Length-point-1;
        if(left>rule.Left || right>rule.Right || left+right>rule.Total || rule.PointRequired && point<0)
            throw new QbException("invalid_arguments",path+": value exceeds the documented SDK numeric format or omits its required decimal point.");
    }
    public static JsonObject Discover()=>new()
    {
        ["types"]=new JsonObject
        {
            ["PRICETYPE"]=new JsonObject {["protocolBelow8"]=Format(8,5,13,true),["protocol8AndAbove"]=Format(10,5,15,false)},
            ["PERCENTTYPE"]=new JsonObject {["protocolBelow8"]=Format(7,5,12,false),["protocol8AndAbove"]=Format(10,5,15,false)},
            ["QUANTYPE"]=new JsonObject {["protocolBelow8"]=Format(8,5,13,false),["protocol8AndAbove"]=Format(10,5,15,false)},
            ["FLOATTYPE"]=Format(8,6,9,false)
        },
        ["interpretation"]="Limits count transmitted digits, excluding sign/decimal point. Leading/trailing zero digits count. Existing invariant decimal lexical validation and per-field minima/maxima still apply. Input is preserved exactly; no rounding, truncation or scientific notation. PERCENTTYPE may be negative or exceed100 unless that field has a separate constraint. These rules do not imply a particular field, product or country is available.",
        ["amountReferenceConflict"]="AMTTYPE remains on existing decimal/per-field-bound validation: official OSR text says two fractional digits but also repeats a 10.5/8.5 format; SDK13 release notes list13.2 for protocol8+. No universal AMTTYPE digit/scale restriction is inferred from that conflicting evidence.",
        ["sources"]=new JsonArray("https://static.developer.intuit.com/qbSDK-current/common/newosr/qbsdk/html/PRICETYPE.html",
            "https://static.developer.intuit.com/qbSDK-current/common/newosr/qbsdk/html/PERCENTTYPE.html",
            "https://static.developer.intuit.com/qbSDK-current/common/newosr/qbsdk/html/QUANTYPE.html",
            "https://static.developer.intuit.com/qbSDK-current/common/newosr/qbsdk/html/FLOATTYPE.html",
            "https://static.developer.intuit.com/qbSDK-current/common/newosr/qbsdk/html/AMTTYPE.html",
            "https://static.developer.intuit.com/qbSDK-current/doc/pdf/ReleaseNotes.pdf")
    };
    private static JsonObject Format(int left,int right,int total,bool point)=>new()
        {["maximumIntegerDigits"]=left,["maximumFractionDigits"]=right,["maximumTotalDigits"]=total,["decimalPointRequired"]=point};
}
