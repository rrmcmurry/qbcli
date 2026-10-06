using System.Text.Json.Nodes;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SdkNumericTypesTests
{
    [Theory]
    [InlineData("PRICETYPE","9999999999.99999",8,true)]
    [InlineData("PRICETYPE","10000000000.00000",8,false)]
    [InlineData("PRICETYPE","1.000001",8,false)]
    [InlineData("PRICETYPE","99999999.99999",7,true)]
    [InlineData("PRICETYPE","100000000.00000",7,false)]
    [InlineData("PRICETYPE","1",7,false)] [InlineData("PRICETYPE","1",8,true)]
    [InlineData("PERCENTTYPE","-125.12345",13,true)]
    [InlineData("PERCENTTYPE","9999999.99999",7,true)]
    [InlineData("PERCENTTYPE","10000000.00000",7,false)]
    [InlineData("PERCENTTYPE","10000000.00000",8,true)]
    [InlineData("QUANTYPE","+9999999999.99999",17,true)]
    [InlineData("QUANTYPE","99999999.99999",7,true)]
    [InlineData("QUANTYPE","100000000.00000",7,false)]
    [InlineData("FLOATTYPE","12345678.1",13,true)]
    [InlineData("FLOATTYPE","12345678.12",13,false)]
    [InlineData("FLOATTYPE","123456789",13,false)]
    [InlineData("FLOATTYPE","123.123456",13,true)]
    [InlineData("FLOATTYPE","1.1234567",13,false)]
    [InlineData("QUANTYPE","00000000001.0",13,false)]
    [InlineData("QUANTYPE","1.000000",13,false)]
    [InlineData("AMTTYPE","1.123456",13,true)]
    public void DocumentedTypeFormatsDistinguishProtocolBoundariesAndUnresolvedAmountType(string type,string value,int protocol,bool valid)
    {
        if(valid)SdkNumericTypes.Validate(type,value,protocol,"test");
        else Assert.Equal("invalid_arguments",Assert.Throws<QbException>(()=>SdkNumericTypes.Validate(type,value,protocol,"test")).Code);
    }
    [Fact] public void BuilderPreservesPermittedPriceTextAndRejectsExcessPrecision()
    {
        JsonObject Fields(string price)=>new(){["ItemServiceAdd"]=new JsonObject {["Name"]="Synthetic",["SalesOrPurchase"]=new JsonObject
            {["Price"]=price,["AccountRef"]=new JsonObject {["FullName"]="Income"}}}};
        var xml=SdkXmlBuilder.Request("ItemServiceAdd",Fields("+1.12345"),"test");
        Assert.Equal("+1.12345",xml.Descendants("Price").Single().Value);
        Assert.Throws<QbException>(()=>SdkXmlBuilder.Request("ItemServiceAdd",Fields("1.123456"),"test"));
    }
    [Fact] public void FullDiscoveryExposesNumericRulesAndAmountConflictWithoutConnection()
    {
        var contract=CommandCatalog.Discover()["sdkNumericContract"]!;
        Assert.Equal(9,contract["types"]!["FLOATTYPE"]!["maximumTotalDigits"]!.GetValue<int>());
        Assert.Contains("conflicting evidence",contract["amountReferenceConflict"]!.GetValue<string>());
    }
    [Theory] [InlineData("Quantity","1.123456")] [InlineData("RatePercent","12.123456")]
    public void BuilderRejectsOverprecisionQuantityAndPercentageOnTheirRealSchemaFields(string field,string value)
    {
        var line=new JsonObject {["ItemRef"]=new JsonObject {["FullName"]="Item"},[field]=value};
        var fields=new JsonObject {["InvoiceAdd"]=new JsonObject {["CustomerRef"]=new JsonObject {["FullName"]="Customer"},["InvoiceLineAdd"]=line}};
        Assert.Throws<QbException>(()=>SdkXmlBuilder.Request("InvoiceAdd",fields,"test"));
    }
    [Fact] public void BuilderRejectsExchangeRateWithTooManyTotalDigits()
    {
        var fields=new JsonObject {["CheckAdd"]=new JsonObject {["AccountRef"]=new JsonObject {["FullName"]="Bank"},["ExchangeRate"]="12345678.12"}};
        Assert.Throws<QbException>(()=>SdkXmlBuilder.Request("CheckAdd",fields,"test"));
        fields["CheckAdd"]!["ExchangeRate"]="123.123456";
        Assert.Equal("123.123456",SdkXmlBuilder.Request("CheckAdd",fields,"test").Descendants("ExchangeRate").Single().Value);
    }
}
