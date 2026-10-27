namespace MarketPilot.Web.Integrations.Trendyol;

// Satıcı sevkiyat/iade/fatura adresleri ve ülke-il-ilçe-mahalle referans servisleri.
public static class TrendyolAddressesPanel
{
    public const string Key = "addresses";
    private const string Member = "member/countries";

    public static readonly TrendyolPanel Panel = new(Key, "Adres Bilgileri", "Satıcı adresleri ile ülke, il, ilçe ve mahalle kod listeleri.",
    [
        new("addresses.seller", Key, "Satıcı iade ve sevkiyat adresleri", "GET", "sellers/{sellerId}/addresses")
            { StoreFrontHeader = true, Note = "Trendyol bu serviste düşük hız sınırı uygular.", Doc = "i̇ade-ve-sevkiyat-adres-bilgileri-getsuppliersaddresses" },
        new("addresses.countries", Key, "Ülkeler", "GET", Member) { Doc = "adres-bilgileri" },
        new("addresses.countryCities", Key, "GULF / CEE ülke illeri", "GET", $"{Member}/{{countryCode}}/cities") { Doc = "adres-bilgileri" },
        new("addresses.countryDistricts", Key, "GULF ülke ilçeleri", "GET", $"{Member}/{{countryCode}}/cities/{{cityId}}/districts") { Doc = "adres-bilgileri" },
        new("addresses.domesticCities", Key, "Türkiye / Azerbaycan illeri", "GET", $"{Member}/domestic/{{countryCode}}/cities") { Note = "countryCode: TR veya AZ.", Doc = "adres-bilgileri" },
        new("addresses.domesticDistricts", Key, "Türkiye / Azerbaycan ilçeleri", "GET", $"{Member}/domestic/{{countryCode}}/cities/{{cityCode}}/districts") { Note = "countryCode: TR veya AZ.", Doc = "adres-bilgileri" },
        new("addresses.neighborhoods", Key, "Türkiye mahalleleri", "GET", $"{Member}/domestic/TR/cities/{{cityCode}}/districts/{{districtCode}}/neighborhoods") { Doc = "adres-bilgileri" },
    ]);
}
