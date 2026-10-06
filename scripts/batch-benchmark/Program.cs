using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
var calls=Enumerable.Range(0,5000).Select(i=>new SdkCall(i.ToString(),"ClassAdd",new JsonObject{
    ["ClassAdd"]=new JsonObject{["Name"]="Synthetic benchmark "+i}})).ToArray();
var prepared=SdkBatch.Prepare(new("13.0","US","stopOnError",calls,1000,16_777_216));
SdkBatch.Messages(prepared); // JIT the new code before measurement.
List<(int Start,string Xml)> Original()
{
    var result=new List<(int,string)>();int start=0;
    while(start<prepared.Requests.Count)
    {
        var batch=new List<XElement>();string xml="";
        while(start+batch.Count<prepared.Requests.Count && batch.Count<prepared.Plan.BatchSize)
        {
            batch.Add(prepared.Requests[start+batch.Count]);
            var candidate=SdkBatch.Message(prepared.Plan,batch);
            if(Encoding.UTF8.GetByteCount(candidate)>prepared.Plan.MaximumBatchBytes){batch.RemoveAt(batch.Count-1);break;}
            xml=candidate;
        }
        result.Add((start,xml));start+=batch.Count;
    }
    return result;
}
var watch=Stopwatch.StartNew();var original=Original();double before=watch.Elapsed.TotalMilliseconds;
watch.Restart();var current=SdkBatch.Messages(prepared);double after=watch.Elapsed.TotalMilliseconds;
if(!original.SequenceEqual(current))throw new Exception("Batch output changed");
var metadata=new{requestCount=calls.Length,batchSize=1000,messageSets=current.Count,
    previousAlgorithmMilliseconds=before,currentAlgorithmMilliseconds=after,exactXmlEquivalent=true,
    scope="Local in-process batch serialization benchmark only. No SDK connection or company mutations; not QuickBooks transaction throughput."};
string json=JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true});
File.WriteAllText("artifacts/fixtures/batch-serialization-benchmark.json",json);Console.WriteLine(json);
