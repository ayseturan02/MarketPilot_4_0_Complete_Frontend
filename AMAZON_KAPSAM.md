# Amazon Türkiye Merkezi — 27 Ekim 2026

Amazon için ayrı SP-API adaptörü ve işletme ekranı eklendi. Trendyol/Hepsiburada servis sözleşmeleri Amazon'a kopyalanmadı. Gerçek satıcı hesabı, veritabanı veya dış sisteme yazma işlemi kurulmadı.

## Amazon'a özgü yapı

- Türkiye pazaryeri `A33AVAJ2PDY3EV`; Avrupa bölgesi. Üretim `sellingpartnerapi-eu.amazon.com`, test `sandbox.sellingpartnerapi-eu.amazon.com`.
- LWA refresh-token akışı. Erişim anahtarı sunucuda bellek önbelleğinde tutulur, tarayıcıya/veri dosyasına/loga gönderilmez. Kimlik bilgileri değişirse önbellek kullanılmaz.
- Satıcı SKU'su, ASIN, barkod ve FNSKU farklı kimliklerdir. ASIN ürün kataloğunu, satıcı SKU'su satıcının ilanını temsil eder.
- FBM satıcı stoğu ile FBA Amazon depo stoğu birleştirilmez. Listing yanıtındaki `DEFAULT` stoğu FBM olarak okunur; Amazon kanal miktarı FBM stoğu yerine gösterilmez.
- Orders API **2026-01-01** kullanılır. FBA siparişlerinde satıcı hazırlama/kargoya verme düğmeleri uygulanmaz.
- Alıcı adı/adresi özellikle talep edilmez (`BUYER`/`RECIPIENT` eklenmez). Eksik bilgiler boş/izin gerektiren olarak gösterilir. Yeni Orders sürümünün kişisel veri erişimi rol bazındadır; bu çalışma kişisel veri rollerini açmaz.

## Yazılmış gerçek okuma desteği

| Bölüm | Resmî işlem / sürüm | Kapsam |
| --- | --- | --- |
| Bağlantı testi | Sellers v1 `getMarketplaceParticipations` | Türkiye satış katılımı doğrulanır |
| Ürünler / FBM stok-fiyat | Listings Items 2021-08-01 `searchListingsItems` | SKU/ASIN/EAN/GTIN/FNSKU/UPC ile tam kimlik sorgusu, durum/sorun/güncelleme tarihleri, fiyat, özellik ve ilan sorunları |
| Siparişler / paketler | Orders 2026-01-01 `searchOrders`, `getOrder` | Tarih/durum/FBA-FBM filtreleri, kalemler, tutarlar ve sunulan paket bilgileri |
| Amazon depoları | FBA Inventory v1 `getInventorySummaries` | Satılabilir, rezerve, yoldaki, satılamaz ve toplam miktarlar; SKU filtresi |
| Finans | Finances 2024-06-19 `listTransactions` | Tarih/sipariş/durum filtreleri, tutar ve ilgili sipariş |
| Raporlar | Reports 2021-06-30 `getReports` | İlan, sipariş ve FBA müşteri iade raporlarının mevcut durumları |
| Marka ve kategori | Product Type Definitions 2020-09-01 `searchDefinitionsProductTypes` | Türkiye için ürün türü arama; tam özellik şeması/eşleştirme değildir |

Hepsi yerel HTTP fixture testleriyle doğrulandı; gerçek satıcıda/SIT'te çalıştığı henüz doğrulanmadı. Bölge, rol, uygulama ve gönderim programı izinleri gerçek hesapla ayrıca sınanmalıdır.

## Çalışan demo

14 bölüm: Genel bakış, Ürünler, Siparişler, Stok ve fiyat, Amazon depoları (FBA), Kargo, İadeler, Finans, Raporlar, Marka ve kategori, Fatura ve garanti, Etiketler, Adresler, İşlem geçmişi.

- 8 tekstil ilanı, 6 sipariş, 3 ayrı FBA stok örneği, iadeler/finans hareketleri/örnek rapor durumları.
- Demo ASIN/FNSKU değerleri gerçek Amazon ürün kaydı değildir. Demo açıklaması ekranlarda yer alır.
- Satıcı stok/fiyat değişikliği ve FBM sipariş ilerletme mevcut kalıcı yerel kuyruğu kullanır; mağazalar ayrıdır.
- FBA ürününün stoğunu değiştirme ve FBA siparişini satıcı gibi ilerletme **sunucuda da reddedilir**.
- Ürün özellikleri, varyant örnekleri, ilan sorunları, paketler, örnek fatura, satıcı iadesi, iş geçmişi.
- Paket kartı ve adresler açıkça demo olarak etiketlidir. Paket kartı gerçek Amazon/taşıyıcı etiketi değildir; rapor durumu dosya üretildiği/indirildiği anlamına gelmez.
- Gerçek mağaza seçildiğinde bu örnek belgeler/adresler gösterilmez.

## Sayfalama ve büyük katalog sınırları

- Listings araması native 20 kayıtlık sayfa ve `pageToken` kullanır. **Amazon tek aramada en fazla 1.000 SKU'yu sayfalar.** Sonuç sayısı daha yüksek olduğunda ekranda uyarı görünür; kimlik/tarih filtreleriyle sorgu daraltılmalıdır. Yüz binlerce ilanı eksiksiz almak için rapor indirme/içe aktarma veya bölümlenmiş eşitleme ayrıca geliştirilmelidir.
- Orders devamında `paginationToken` ve aynı tarih/filtreler korunur; filtre değişince token geçmişi temizlenir. Sipariş/finans tarihleri Amazon'un güncel veri gecikmesi nedeniyle en az üç dakika geriden sınırlandırılır.
- Reports devamında `nextToken` tek başına gönderilir. Finances boş sayfada devam bilgisi verebilir; boş sayfa son sayfa sayılmaz.
- FBA ve finansın servis tarafından belirlenen sayfaları kullanılır; tüm katalog/siparişler tarayıcıya indirilmez.
- Tarayıcıda mağaza/bölüm başına yalnız güncel sonuç ve en fazla 200 devam bilgisi tutulur. Tekrarlanan devam bilgisi reddedilir. Eski sipariş token'ları için yerel 23 saat sıfırlaması vardır.
- İstekler mağaza başına sıraya alınır. Oran sınırı hatasında otomatik döngüyle istek yağdırılmaz; sunucu kısa bekleme uygular.

## Gerçek bağlantıda eksik işler

İlan oluşturma/güncelleme, stok-fiyat gönderimi, Feeds ve gönderim sonucu takibi; tam katalog/özellik şeması eşleştirme; rapor oluşturma/indirme/içe aktarma; kargo satın alma/etiket/teslimat bildirimi; FBA inbound/depo sevkiyatı; gerçek iade/geri ödeme; fatura yükleme ve garanti belgeleri; mağaza adresleri; finans bakiyesi/mutabakat; Notifications ve sürekli senkronizasyon tamamlanmadı.

İadeler gerçek mağazada tek bir onay ekranı gibi sunulmaz. FBA müşteri iadeleri için mevcut rapor durumlarına yönlendirme vardır. Rapordan gerçek iade satırları henüz içe alınmaz.

## Sunucu yapılandırması

Her ortam için `Marketplaces:Amazon:{stage|production}` altında `SellerId`, `ClientId`, `ClientSecret`, `RefreshToken` gerekir. Değerler sohbete veya mağaza formuna yazılmamalı, Git'e eklenmemelidir. Uygulama kaydı ve satıcı yetkilendirmesi Amazon tarafında tamamlanmalıdır. Her ortamda tek sunucu hesabı kullanılır; farklı mağaza adları bağımsız kimlik bilgileri oluşturmaz.

Sabit HTTPS hostları, yönlendirme kapatma, 45 saniye istek süresi ve 10 MB yanıt sınırı uygulanır. Ham hata gövdesi veya anahtar kullanıcıya dönmez. Yeni GET uçları mevcut yerel oturum/rol korumasına tabidir. Gerçek pazar yeri yazması sunulmaz; dış POST yalnız LWA erişim anahtarı alışverişidir.

## Testler

- `dotnet run --project tests/MarketPilot.AmazonChecks -p:BuildProjectReferences=false`: resmî şemalara göre hazırlanmış yanıtlar; LWA önbelleği, EU/TR, kimlikler, stok ayrımı, güncel sipariş, token'lar ve güvenli hatalar.
- `node tests/amazon-protocol-checks.cjs`: sabit tarih, devam bilgisi, filtre sıfırlama, tekrarlanan token ve sınırlı geçmiş.
- `node tests/amazon-http-checks.cjs`: ayrı geçici veri kökü/port ile tarayıcı/HTTP; ana kullanıcıları/verileri değiştirmez. Chrome ve mevcut yerel Playwright kurulumunu kullanır.

## Resmî kaynaklar

- [Pazaryeri kimlikleri](https://developer-docs.amazon/sp-api/docs/marketplace-ids)
- [Bölge uçları](https://developer-docs.amazon/sp-api/docs/sp-api-endpoints)
- [LWA bağlantısı](https://developer-docs.amazon.com/sp-api/docs/connecting-to-the-selling-partner-api)
- [Orders 2026 ve geçiş rehberi](https://developer-docs.amazon.com/sp-api/docs/orders-api-migration-guide)
- [Resmî SP-API modelleri](https://github.com/amzn/selling-partner-api-models)
- [Listings araması](https://developer-docs.amazon.com/sp-api/reference/searchlistingsitems)
- [FBA Inventory](https://developer-docs.amazon.com/sp-api/docs/fba-inventory-api)
- [Finans işlemleri](https://developer-docs.amazon.com/sp-api/reference/listtransactions)

İncelenen şema dosyaları: `orders_2026-01-01.json`, `listingsItems_2021-08-01.json`, `fbaInventory.json`, `finances_2024-06-19.json`, `reports_2021-06-30.json`, `sellers.json`, `definitionsProductTypes_2020-09-01.json` (Amazon'un resmî deposu).

## Mağaza kimliği ve stok/fiyat yazma güncellemesi

Mağazaya ayrı sunucu kimlik profili seçimi ve kalıcı stok/fiyat gönderimi eklendi. Bu belgedeki önceki salt okuma sınırları stok/fiyat için güncellenmiştir; diğer yazma işlemleri kapsam dışıdır. Güncel sözleşme, izin ve test sınırları: [MAGAZA_HESAPLARI_VE_YAZMA.md](MAGAZA_HESAPLARI_VE_YAZMA.md).
