# ERP kaynaklı fiyat yönetimi

Ana fiyat yetkisi Nebim V3 ERP'dedir. `PriceList`, `PriceEntry` ve `RecordContext` MarketPilot'un ortak sözleşmeleridir; Nebim'in gerçek tablo, alan, servis veya hesaplama tanımları değildir. Gerçek adaptör, kurulumun doğrulanmış sözleşmesi sağlanmadan etkinleştirilmez. Şu anda yalnız mock ERP okunur ve mock pazaryerlerine fiyat uygulanır.

`/api/pricing` durum, kaynak listeleri, kanal kuralları ve son 100 önizlemeyi döndürür. `/source/refresh` tam mock snapshot okur. `PUT /policy` işletme politikasını, `PUT /rules` mağaza/satıcı bazında liste eşleştirmesini kaydeder. Fiyat ve politika kayıtları işletmenin mevcut kalıcı veri deposunda tutulur. Yapılandırılmış hesaplarda değişiklikler yönetici yetkisi gerektirir.

## Hesaplama

Kaynakta KDV dahilse açık KDV oranıyla nete dönülür. Kaynak KDV oranı varsa kullanılır; yoksa kanalın açıkça yapılandırılmış oranı gerekir. Net tutara sabit net masraf ve yapılandırılmış artış uygulanır; komisyon ve değişken masraf yapılandırılan matraha göre hesaplanır. Hedef KDV dahil/hariç kuralı uygulanır. Sonuç iki ondalıkla, yarım değerler sıfırdan uzağa yuvarlanır. Bunlar MarketPilot işletme kurallarıdır; Nebim yöntemi olarak sunulmaz.

Etkin kampanyalardan en yüksek öncelikli tek kampanya uygulanır. İndirimler birikmez. Aynı en yüksek öncelikte iki etkin kampanya varsa işlem engellenir. Başlangıç dahildir, bitiş dahil değildir.

Eksik fiyat `null` kalır. Sıfır veya negatif fiyat, bilinmeyen KDV, para birimi uyuşmazlığı, birden çok varyant fiyatı ve min/max sınır ihlali yayını engeller. Döviz dönüşümü ve fiyat kırpma yapılmaz. Minimum ve maksimumlar kanal bazında yapılandırılır.

## Önizleme ve onay

`POST /preview` için `productId`, `storeId`, `listingKey` gerekir. Kaynak fiyat, mevcut fiyat, hesaplanan fiyat, tutar/yüzde farkı, kampanya, kaynak/kural sürümü ve engel nedeni saklanır. Varsayılan olarak her değişiklik onay ister. Politika, yüzde eşiğiyle onay gereksinimini ve önizleme/kaynak geçerlilik süresini değiştirebilir.

`POST /proposals/{id}/approve` onaylayan kişiyi kaydeder. `/publish` yalnız mock ilana uygular. Kaynak sürümü/içeriği, kural, kampanya, ürün, mağaza veya ilan fiyatı değişirse yeniden önizleme gerekir. Eski kaynak sürümü ve aynı sürümde farklı içerik reddedilir. Kaybolan liste silinmez; eksik olarak işaretlenir ve yayını durdurur.

Mock yayın makbuzu aynı önizleme kimliğiyle tekilleştirilir ve yeniden başlatma sonrasında korunur. Gerçek fiyat gönderimi yapılandırma bayraklarıyla açılamaz. Eski birleşik stok/fiyat gönderim akışı ERP onayını atlayamaz. Stok işleri onaylı fiyatı değiştirmez.

## Doğrulama

`dotnet run --project tests/MarketPilot.PricingChecks` hesaplama, güvenlik, kampanya, tüm mock kanallar, onay, sürüm, eskime, tekrar ve kalıcılık senaryolarını çalıştırır.

`dotnet build src/MarketPilot.Web` ardından `node tests/pricing-http-checks.cjs` HTTP ve tarayıcı üzerinden önizleme/onay/yayın, yetki ve yeniden başlatmayı doğrular. Testler geçici dizin ve mock sağlayıcı kullanır; canlı kimlik bilgileri temizlenir.
