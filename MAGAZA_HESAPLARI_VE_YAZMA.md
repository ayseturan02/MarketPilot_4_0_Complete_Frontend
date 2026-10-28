# Mağaza hesapları ve stok/fiyat yazma

Her gerçek mağaza artık `credentialProfile` ile ayrı sunucu profiline bağlanır. Aynı kanal/ortamda farklı profiller farklı satıcılardır. Profil pazaryeri ve ortamıyla eşleşmezse istek gönderilmez; başka hesabın anahtarlarına geri dönülmez. Aynı profilin ikinci kez mağaza olarak eklenmesi reddedilir. Eski kayıtlardaki profilsiz bağlantılar mevcut `Marketplaces:{kanal}:{ortam}` ayarlarıyla geriye uyumlu çalışır. Yeni gerçek mağaza açık profil seçmelidir.

API sırları tarayıcıya, bağlantı dosyasına ve gönderim kuyruğuna yazılmaz. `GET /api/integrations/credential-profiles` yalnızca kimlik, görünen ad, kanal, ortam ve hazırlık/yazma durumunu döndürür. Amazon OAuth önbelleği ve Ticimax servis sözleşmesi önbelleği hesap bazında ayrıdır. Hesap bilgisi değişmiş eski kuyruk işleri durdurulur. Ürün stok karşılaştırması, ortak siparişler, webhook satıcı doğrulaması ve zamanlayıcı da profili dikkate alır.

## Profilleri daha sonra tanımlama

`src/MarketPilot.Web/appsettings.Accounts.example.json` alan isimlerini gösterir; kendiliğinden yüklenmez ve sır içermez. Gerçek değerleri kaynak koduna koymadan user-secrets (Development) veya ortam değişkenleriyle tanımlayın:

```powershell
dotnet user-secrets set "MarketplaceAccounts:trendyol-magaza-1:Marketplace" "trendyol" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-magaza-1:Environment" "production" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-magaza-1:Label" "Ana mağaza" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-magaza-1:Credentials:SellerId" "SATICI_ID" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-magaza-1:Credentials:ApiKey" "API_KEY" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-magaza-1:Credentials:ApiSecret" "API_SECRET" --project src/MarketPilot.Web
```

İkinci satıcı için farklı profil adı kullanılır. Üretimde örneğin `MarketplaceAccounts__trendyol-magaza-2__Credentials__ApiKey` ortam değişkeni kullanılır. Satış Kanalları → Mağaza ekle → Gerçek mağaza → Satıcı kimlik profili.

## Yazma kapsamı

Buradaki destek mevcut ürün/varyantın **stok ve satış fiyatı** içindir. Ürün oluşturma/silme, sevkiyat ve fatura yazma bu değişikliğin kapsamında değildir.

| Kanal | İşlem ve kimlik | Koruma |
|---|---|---|
| Trendyol | price-and-inventory, barkod | Stok-only istekte fiyat alanları gönderilmez. |
| Hepsiburada | inventory-uploads, Hepsiburada SKU + merchantSku | Listing ID, HB SKU yerine kullanılmaz; fiyat boşsa gönderilmez. |
| n11 | price-stock-update, stockCode | n11depom stoğu reddedilir; fiyat değişiminde listPrice/salePrice birlikte gider. |
| Amazon TR | Listings Items PATCH, satıcı SKU | DEFAULT satıcı stoğuna merge; FBA ve çok konumlu stok reddedilir. Fiyat değişiminde TRY/ALL teklif seçilir. Kampanya/otomatik fiyat varsa işlem durur. |
| Boyner | price-and-inventory, barkod | Zorunlu liste/satış fiyatı stok-only işlemde korunur. Bilinmeyen fiyatla gönderilmez. |
| Ticimax | WSDL ile doğrulanan VaryasyonGuncelle, varyasyon ID | Tüm ilgisiz alan güncelleme bayrakları false. Fiyat hedefi eski indirimi temizler. Uyumlu servis yoksa işlem durur. |

Gerçek gönderim için **hem** `Synchronization:AllowRealWrites=true` **hem** seçilen profilin `MarketplaceAccounts:{profil}:AllowWrites=true` olması gerekir. Varsayılan kapalıdır. Zamanlayıcının gerçek hesapları okuması ayrıca `Synchronization:IncludeRealStores=true` gerektirir. Bu geliştirme sırasında gerçek hesap anahtarı tanımlanmadı ve canlı satış verisi değiştirilmedi.

Satış Kanalları → Stok / fiyat gönder veya ortak katalog → hedefleri önizle → uygula. SKU, barkod, tam sayı stok ve isteğe bağlı en fazla iki ondalıklı fiyat verilir. Boş fiyat mevcut fiyatı korur. `POST /api/integrations/connections/{id}/stock-price` açık `confirm:true` ve tekrar gönderimlere karşı `requestId` ister. HTTP 202 yalnızca kalıcı kuyruğa alındığını belirtir. Aynı işlem kimliğiyle farklı hedef reddedilir.

Gönderimden önce ürün kimliği doğrulanır, kalıcı kayda alınır. API kabulü sonrasında mağazadan tekrar okunup hedef stok/fiyat eşleşince tamamlanır. 429 artan beklemeyle tekrar denenir. Bağlantı kesilmesi veya yeniden başlatmada sonucu belirsiz gerçek gönderim tekrar gönderilmeden doğrulanır. Doğrulanamayan iş başarı sayılmaz. Katalogdan gerçek fiyat yayını için para birimi ve KDV bilgisi açıkça bilinmelidir.

## Test ve canlı doğrulama sınırı

Hesap ayrımı ve yazma gövdeleri sahte HTTP/SOAP servisleriyle sınanır. Gerçek satıcıların API yetkileri, Ticimax mağazasının WSDL sürümü ve canlı kabul/ret davranışı, gerçek bağlantılar son aşamada tanımlanırken ayrıca doğrulanmalıdır. Hepsiburada resmî dokümantasyon portalının yazma şemalarına bu çalışma ortamından erişim güvenlik duvarına takıldı; onun inventory upload sözleşmesi ilk test hesabıyla özellikle doğrulanmalıdır. Bu sebeple canlı kullanım için hatasızlık garantisi verilmez.

Kaynaklar:
- [n11 resmî fiyat/stok sözleşmesi](https://developer.n11.com/documentation/n11-marketplace-entegrasyonu/urun-fiyat-stok-guncelleme/)
- [Amazon Listings Items](https://developer-docs.amazon/sp-api/docs/listings-items-api), [merge](https://developer-docs.amazon/sp-api/docs/merge-a-listing), [teklif fiyatı](https://developer-docs.amazon/sp-api/docs/manage-purchasable-offer)
- [Boyner resmî OpenAPI](https://merchant.boyner.com.tr/api/docs/render)
- [Ticimax resmî servis kapsamı](https://www.ticimax.com/web-servis-api/), [Ticimax mağazasının herkese açık WSDL'i](https://www.ticimax.net/Servis/UrunServis.svc?singleWsdl)
- [Hepsiburada resmî portalı](https://developers.hepsiburada.com/tr/companies/hepsiburada)
