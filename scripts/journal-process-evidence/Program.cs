// Synthetic execution only. No COM, company session or company-file access.
using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Supply a new absolute journal path.");
var calls=Enumerable.Range(1,3).Select(i=>new SdkCall(i.ToString(),"ClassAdd",new JsonObject
    {["ClassAdd"]=new JsonObject {["Name"]="Synthetic process evidence "+i}})).ToArray();
var plan=new SdkPlan("13.0","US","stopOnError",calls,1);
SdkBatch.Execute(SdkBatch.Prepare(plan),@"C:\synthetic\Test.QBW",args[0],xml=>
{
    string id=XDocument.Parse(xml).Descendants("ClassAddRq").Single().Attribute("requestID")!.Value;
    int code=id=="2"?3100:0;
    return $"<QBXML><QBXMLMsgsRs><ClassAddRs requestID='{id}' statusCode='{code}' statusSeverity='{(code==0?"Info":"Error")}'/></QBXMLMsgsRs></QBXML>";
});
Console.WriteLine("finished_but_process_alive"); Console.Out.Flush();
Thread.Sleep(15000); // Simulated cleanup: journal execution finished before process exit.
