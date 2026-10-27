using MarketPilot.Web.Integrations;
using System.Diagnostics;
static void Check(bool value,string message){if(!value)throw new Exception(message);}
var products=Enumerable.Range(0,100000).Select(i=>new MarketplaceProduct(i.ToString(),i.ToString(),"MODEL-"+i,"SKU-"+i,"BAR-"+i,"Ürün "+i,"cat","Kategori","Marka",100+i,90+i,null,i%100,"TRY",i%100>0,"",[],i%2==0?"approved":"pendingApproval",[])).ToArray();
var timer=Stopwatch.StartNew();
var exact=ListingFilters.Products(products,new ListingQuery(Barcode:"BAR-99999",Size:25));Check(exact.TotalElements==1&&exact.Items[0].Sku=="SKU-99999","Exact search missed a record outside first page");
var zero=ListingFilters.Products(products,new ListingQuery(MaxStock:0,Page:4,Size:100,Sort:"priceAsc"));Check(zero.Items.Count==100&&zero.TotalElements==1000&&zero.Items.All(p=>p.Stock==0),"Stock filter must precede paging");
Check(zero.Items.First().SalePrice==40090,"Sort must precede paging");
var approval=ListingFilters.Products(products,new ListingQuery(Approval:"unapproved",Page:100,Size:50));Check(approval.TotalElements==50000&&approval.Items.Count==50&&approval.Items.All(p=>p.ApprovalStatus!="approved"),"Approval filter/paging failed");
var date=DateTimeOffset.Parse("2026-10-27T12:00:00+03:00");
var orders=Enumerable.Range(0,100000).Select(i=>new MarketplaceOrder("PKG-"+i,"ORDER-"+i,i%2==0?"Created":"Shipped",date.AddMinutes(-i),"Müşteri "+i,"",100+i,100+i,0,"TRY","", "",null,[],new("Kargo","TRACK-"+i,"","","",1,1),new("","","",false,"","","",false,[]))).ToArray();
var last=ListingFilters.Orders(orders,new ListingQuery(Search:"ORDER-99999",Size:25));Check(last.TotalElements==1&&last.Items[0].PackageId=="PKG-99999","Order search missed distant record");
var range=ListingFilters.Orders(orders,new ListingQuery(Status:"Created",StartDate:date.AddMinutes(-100).ToUnixTimeMilliseconds(),EndDate:date.ToUnixTimeMilliseconds(),Size:25));Check(range.TotalElements==51&&range.Items.Count==25,"Date/status filter or page limit failed");
var empty=ListingFilters.Products(products,new ListingQuery(Barcode:"absent"));Check(empty.Items.Count==0&&empty.TotalElements==0,"Empty results failed");
Check(new ListingQuery(Size:1000).Error()!=null&&new ListingQuery(Page:int.MaxValue).Error()!=null&&new ListingQuery(MinPrice:10,MaxPrice:1).Error()!=null,"Query validation failed");
Check(new ListingQuery(Size:100,Page:999).Error()==null,"Valid 100k pagination rejected");
Console.WriteLine($"PASS: 100,000 products + 100,000 orders, distant exact match, stock/status/date filters before sort/page, bounded results and invalid query rejection ({timer.ElapsedMilliseconds} ms, in-memory correctness check; not a production load benchmark).");
