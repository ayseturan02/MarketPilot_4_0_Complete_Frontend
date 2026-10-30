using System.Diagnostics;
using System.Text.Json;
using MarketPilot.Web.Wms;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

var root=Path.GetFullPath(args.Length>1?args[1]:Path.Combine(Path.GetTempPath(),"marketpilot-qa-probe-"+Guid.NewGuid().ToString("N")));
if(Path.GetDirectoryName(root)!=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(root).StartsWith("marketpilot-qa-"))throw new Exception("QA temp root required");
Directory.CreateDirectory(root);var environment=new TestEnvironment(root);
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};
if(args.FirstOrDefault()=="backup")
{
    var data=Path.Combine(root,"Data");Directory.CreateDirectory(data);
    var required=new[]{"demo-store.json","tenant-directory.json","credential-references.json","wms-inventory.json","wms-fulfillment.json","wms-logistics.json","integration-monitoring.json"};
    _=new DemoStore(environment).Read();
    foreach(var name in required.Where(name=>name!="demo-store.json"))File.WriteAllText(Path.Combine(data,name),"{}");
    var backups=new LocalBackups(environment);var backup=backups.Create();var verified=backups.Verify(backup.Name);
    var envelope=JsonSerializer.Deserialize<BackupEnvelope>(backups.Download(backup.Name))!;
    var cipher=Convert.FromBase64String(envelope.Cipher);var plain=new byte[cipher.Length];
    using(var aes=new AesGcm(Convert.FromBase64String(File.ReadAllText(Path.Combine(data,"backup.key"))),16))aes.Decrypt(Convert.FromBase64String(envelope.Nonce),cipher,Convert.FromBase64String(envelope.Tag),plain,Encoding.UTF8.GetBytes("InternetTekstilBackup:v1"));
    using var zip=new ZipArchive(new MemoryStream(plain),ZipArchiveMode.Read);
    var included=zip.Entries.Select(entry=>entry.FullName).ToArray();var missing=required.Except(included).ToArray();
    var restoreCheck=backups.RestoreCheck(backup.Name);
    Console.WriteLine(JsonSerializer.Serialize(new{test="backup-module-completeness",passed=missing.Length==0,verified,restoreCheck,included,missing},json));return missing.Length==0?0:1;
}
if(args.FirstOrDefault()=="invariants")
{
    var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Workspace:BusinessId"]="default"}).Build();
    var db=new DemoStore(environment);var canonical=new CanonicalStore(db,config);
    var erp=new MockNebimProvider(db,new("mock-nebim","default",AllowMockWrites:true));
    await new NebimCanonicalSync(erp,canonical,db).Run(default);
    var registry=new IntegrationRegistry(environment);var wms=new MockWmsProvider(new("mock-wms","default",AllowMockWrites:true));
    var inventory=new WmsInventoryService(wms,canonical,registry,environment);await inventory.UpdatePolicyAsync(new(new(),new(),new()));
    var available=(await inventory.ReadAsync()).Stock.Single(row=>row.Warehouse.Id=="mock-main"&&row.Snapshot.Sku=="TSH-EKR-S").SellableQuantity;
    var workflow=new FulfillmentOrchestrationService(wms,inventory,erp,canonical,new MockFulfillmentStatusPublisher(wms.Profile,new()),environment);
    workflow.UpdatePolicy(new(true,[FulfillmentWriteSteps.WmsFulfillmentSubmission],["mock-main"],false,["marketplace","erp"]));
    FulfillmentWorkflowOrder Order(string id)=>new("trendyol","qa-seller",id,id,"Created",DateTimeOffset.UtcNow,"TRY",[new("L1","TSH-EKR-S",30,"EA",449,"TRY")]);
    var orders=await Task.WhenAll(workflow.ReceiveAsync(Order("qa-order-a")),workflow.ReceiveAsync(Order("qa-order-b")));
    var allocated=orders.Sum(order=>order.Warehouses.Sum(warehouse=>warehouse.Lines.Sum(line=>line.Quantity)));
    var accepted=new List<string>();foreach(var order in orders){await workflow.AdvanceAsync(order.Id);accepted.Add((await workflow.AdvanceAsync(order.Id)).State);}
    var result=new{test="cross-order-stock-allocation",passed=allocated<=available,available,allocated,orderStates=orders.Select(order=>order.State),acceptanceStates=accepted,expected="Total active allocation must not exceed the approved sellable balance"};
    Console.WriteLine(JsonSerializer.Serialize(result,json));return result.passed?0:1;
}
var metrics=new List<object>();
foreach(var size in new[]{100,1000,10000})
{
    var folder=Path.Combine(root,"size-"+size);Directory.CreateDirectory(folder);var db=new DemoStore(new TestEnvironment(folder));
    var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime.TotalMilliseconds;var watch=Stopwatch.StartNew();
    db.Modify(data=>{data.Products.AddRange(Enumerable.Range(0,size).Select(i=>new Product{Id="qa-product-"+i,Sku="QA-SKU-"+i,Barcode="990000"+i,Name="QA synthetic clothing "+i,Category="Giyim / Tişört",Status="Aktif",SourceSystem="nebim-v3",SourceMode="demo",Stock=i%100,StockKnown=true}));data.Catalog.Version=1;return true;});
    var writeMs=watch.Elapsed.TotalMilliseconds;var samples=new List<double>();
    for(var i=0;i<30;i++){watch.Restart();var page=db.SearchProducts(new(Search:"QA synthetic",Size:25,Page:i%4));if(page.TotalElements!=size||page.Items.Count!=25)throw new Exception("Synthetic page integrity failed");samples.Add(watch.Elapsed.TotalMilliseconds);}
    samples.Sort();process.Refresh();metrics.Add(new{size,writeMs,p50Ms=samples[15],p95Ms=samples[28],maxMs=samples[^1],workingSetBytes=process.WorkingSet64,privateBytes=process.PrivateMemorySize64,cpuMs=process.TotalProcessorTime.TotalMilliseconds-cpu,fileBytes=new FileInfo(Path.Combine(folder,"Data/demo-store.json")).Length});
    if(process.WorkingSet64>512L*1024*1024)break;
}
Console.WriteLine(JsonSerializer.Serialize(new{test="bounded-synthetic-performance",metrics,serverContentRoot=Path.Combine(root,"size-10000")},json));return 0;

sealed class TestEnvironment(string root):IWebHostEnvironment
{
    public string EnvironmentName{get;set;}="Production";public string ApplicationName{get;set;}="QaProbes";
    public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
    public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
}
