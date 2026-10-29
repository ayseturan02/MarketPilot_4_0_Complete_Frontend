using MarketPilot.Web.Contracts;
namespace MarketPilot.Web.Pricing;

public sealed record PricingPolicy(string? MasterPriceListId=null,string[]? AllowedCurrencies=null,bool AlwaysRequireApproval=true,decimal ApprovalChangePercent=5,int PreviewMinutes=10,int SourceMaxAgeMinutes=60);
public sealed record PriceCampaign(string Id,int Priority,decimal DiscountPercent,DateTimeOffset StartsAt,DateTimeOffset EndsAt,bool Enabled=true);
// These are configurable MarketPilot rules, not native Nebim fields or formulas.
public sealed record ChannelPriceRule(string StoreId,string PriceListId,string Currency,bool VatIncluded=true,decimal? VatRate=null,
    decimal CommissionPercent=0,decimal VariableCostPercent=0,decimal FixedCostNet=0,decimal MarkupPercent=0,
    decimal MinimumPrice=0.01m,decimal MaximumPrice=10000000,PriceCampaign[]? Campaigns=null,string PercentageCostBasis="net");
public sealed record PriceCalculation(decimal? SourceAmount,decimal? NetBeforeFees,decimal? CalculatedAmount,string Currency,string? CampaignId,string? BlockReason);
public sealed class PricingState
{
    public PricingPolicy Policy{get;set;}=new();public long RuleVersion{get;set;}
    public Dictionary<string,ChannelPriceRule> Rules{get;set;}=[];
    public Dictionary<string,string> VariantSources{get;set;}=[];
    public Dictionary<string,PriceList> Lists{get;set;}=[];
    public HashSet<string> MissingLists{get;set;}=[];
    public DateTimeOffset? LastSourceReadAt{get;set;}
    public List<PriceProposal> Proposals{get;set;}=[];
}
public sealed class PriceProposal
{
    public string Id{get;set;}=Guid.NewGuid().ToString("N");public string ProductId{get;set;}="";public string StoreId{get;set;}="";public string ListingKey{get;set;}="";
    public string Fingerprint{get;set;}="";public string SourceListId{get;set;}="";public long SourceVersion{get;set;}public long RuleVersion{get;set;}
    public decimal? SourceAmount{get;set;}public decimal? OldPrice{get;set;}public decimal? NewPrice{get;set;}public string Currency{get;set;}="";public string? CampaignId{get;set;}public string? Error{get;set;}
    public string State{get;set;}="pending-approval";public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow;public DateTimeOffset ExpiresAt{get;set;}
    public string? ApprovedBy{get;set;}public DateTimeOffset? ApprovedAt{get;set;}public string? ReceiptId{get;set;}
    public decimal? Difference=>NewPrice-OldPrice;
    public decimal? DifferencePercent {get{try{return OldPrice>0?Difference/OldPrice*100:null;}catch(OverflowException){return null;}}}
}
public static class PriceCalculator
{
    public static PriceCalculation Calculate(PriceEntry? entry,ChannelPriceRule rule,DateTimeOffset now)
    {
        PriceCalculation Block(string why)=>new(entry?.Amount,null,null,rule.Currency,null,why);
        if(rule.CommissionPercent is <0 or >=100||rule.VariableCostPercent is <0 or >=100||rule.CommissionPercent+rule.VariableCostPercent>=100||rule.FixedCostNet<0||rule.MarkupPercent<0||rule.MinimumPrice<=0||rule.MaximumPrice<rule.MinimumPrice)return Block("Invalid pricing rule.");
        if(rule.PercentageCostBasis is not ("net" or "gross"))return Block("Yüzdesel masraf tabanı net veya brüt olmalı.");
        if((rule.Campaigns??[]).Any(c=>c==null||c.DiscountPercent is <0 or >=100||c.EndsAt<=c.StartsAt))return Block("Invalid campaign rule.");
        if(entry?.Amount==null)return Block("ERP fiyatı bilinmiyor; sıfır fiyat üretilmedi.");
        if(entry.Currency!=rule.Currency)return Block("ERP ve kanal para birimleri farklı; kur dönüşümü yapılmaz.");
        if(entry.Amount<=0||entry.Amount>10000000)return Block("ERP fiyatı pozitif güvenlik aralığında değil.");
        var rate=entry.VatRate??rule.VatRate;
        if(rate is null or <0 or >100)return Block("KDV hesabı için açık oran gerekli.");
        var campaigns=(rule.Campaigns??[]).Where(c=>c.Enabled&&c.StartsAt<=now&&now<c.EndsAt).OrderByDescending(c=>c.Priority).ToArray();
        if(campaigns.Length>1&&campaigns[0].Priority==campaigns[1].Priority)return Block("Aynı öncelikte birden fazla etkin kampanya var.");
        try
        {
            var net=entry.VatIncluded?entry.Amount.Value/(1+rate.Value/100):entry.Amount.Value;
            var fee=(rule.CommissionPercent+rule.VariableCostPercent)/100*(rule.PercentageCostBasis=="gross"?1+rate.Value/100:1);
            if(fee>=1)return Block("Brüt masraf ve KDV birleşimi fiyat hesabına uygun değil.");
            var value=(net+rule.FixedCostNet)*(1+rule.MarkupPercent/100)/(1-fee);
            var campaign=campaigns.FirstOrDefault();if(campaign!=null)value*=1-campaign.DiscountPercent/100;
            if(rule.VatIncluded)value*=1+rate.Value/100;
            value=decimal.Round(value,2,MidpointRounding.AwayFromZero);
            if(value<rule.MinimumPrice||value>rule.MaximumPrice)return Block("Hesaplanan fiyat alt/üst güvenlik sınırının dışında; fiyat kırpılmadı.");
            return new(entry.Amount,net,value,rule.Currency,campaign?.Id,null);
        }catch(OverflowException){return Block("Fiyat hesabı sayısal sınırı aşıyor.");}
    }
}
