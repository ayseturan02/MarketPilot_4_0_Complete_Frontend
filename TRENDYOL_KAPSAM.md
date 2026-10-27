# Trendyol servis kapsamı

27 Ekim 2026. Bu rapor arayüz bağlantısını gösterir; gerçek mağazada başarılı işlem/teslimat doğrulaması değildir.

87 tanım: 70 günlük kullanıcı işlemi, 4 eski sürüm, 7 test, 6 bildirim altyapısı. Eski sürüm, test ve bildirim altyapısı günlük kullanıcı ekranında gösterilmez. Bildirim alıcısı ve test servisleri bu çalışmada etkinleştirilmedi.

70 günlük işlem için normal form/düğme bağlantısı ve Chrome form açılışı kontrol edildi. JSON, servis yolları ve HTTP yöntemleri kullanıcıya gösterilmez. Yazma işlemleri gerçek bağlantı, sunucu izni ve kullanıcı onayı gerektirir. Demo mağazada yazma yapılmaz.

Canlı hesap kimlik bilgileri eksiktir. Gerçek ürün oluşturma/güncelleme, sipariş/iade değişiklikleri, dosya/etiket teslimatı, finans ve platform yanıtları gerçek hesapla ayrıca doğrulanmalıdır. Formların açılması, bu işlemlerin uçtan uca tamamlandığı anlamına gelmez.

| Servis | Kapsam |
|---|---|
| `products.approved` | Kullanıcı işlemi |
| `products.approvedInventoryPrice` | Kullanıcı işlemi |
| `products.unapproved` | Kullanıcı işlemi |
| `products.byBarcode` | Kullanıcı işlemi |
| `products.filterV1` | Eski sürüm — günlük ekranda yok |
| `products.updateAudits` | Kullanıcı işlemi |
| `products.batchResult` | Kullanıcı işlemi |
| `products.buybox` | Kullanıcı işlemi |
| `products.createV2` | Kullanıcı işlemi |
| `products.createV1` | Eski sürüm — günlük ekranda yok |
| `products.updateUnapproved` | Kullanıcı işlemi |
| `products.updateContent` | Kullanıcı işlemi |
| `products.updateVariant` | Kullanıcı işlemi |
| `products.updateDeliveryInfo` | Kullanıcı işlemi |
| `products.archive` | Kullanıcı işlemi |
| `products.unlock` | Kullanıcı işlemi |
| `products.delete` | Kullanıcı işlemi |
| `catalog.brands` | Kullanıcı işlemi |
| `catalog.brandsByName` | Kullanıcı işlemi |
| `catalog.categories` | Kullanıcı işlemi |
| `catalog.categoryAttributes` | Kullanıcı işlemi |
| `catalog.categoryAttributeValues` | Kullanıcı işlemi |
| `catalog.categoryAttributesV1` | Eski sürüm — günlük ekranda yok |
| `catalog.cargoProviders` | Kullanıcı işlemi |
| `catalog.createBrand` | Kullanıcı işlemi |
| `inventory.updatePriceAndInventory` | Kullanıcı işlemi |
| `orders.packages` | Kullanıcı işlemi |
| `orders.packagesV1` | Eski sürüm — günlük ekranda yok |
| `orders.stream` | Kullanıcı işlemi |
| `orders.updatePackage` | Kullanıcı işlemi |
| `orders.unsupplied` | Kullanıcı işlemi |
| `orders.changeCargoProvider` | Kullanıcı işlemi |
| `orders.boxInfo` | Kullanıcı işlemi |
| `orders.warehouse` | Kullanıcı işlemi |
| `orders.laborCosts` | Kullanıcı işlemi |
| `orders.splitMultiPackageByQuantity` | Kullanıcı işlemi |
| `orders.split` | Kullanıcı işlemi |
| `orders.multiSplit` | Kullanıcı işlemi |
| `orders.quantitySplit` | Kullanıcı işlemi |
| `orders.alternativeDelivery` | Kullanıcı işlemi |
| `orders.manualDeliverByTracking` | Kullanıcı işlemi |
| `orders.manualDeliverByPackage` | Kullanıcı işlemi |
| `orders.manualReturnByTracking` | Kullanıcı işlemi |
| `orders.manualReturnByPackage` | Kullanıcı işlemi |
| `orders.deliveredByService` | Kullanıcı işlemi |
| `claims.list` | Kullanıcı işlemi |
| `claims.issueReasons` | Kullanıcı işlemi |
| `claims.audit` | Kullanıcı işlemi |
| `claims.create` | Kullanıcı işlemi |
| `claims.approve` | Kullanıcı işlemi |
| `claims.createIssue` | Kullanıcı işlemi |
| `invoices.sendLink` | Kullanıcı işlemi |
| `invoices.deleteLink` | Kullanıcı işlemi |
| `invoices.sendFile` | Kullanıcı işlemi |
| `invoices.warrantyDocument` | Kullanıcı işlemi |
| `labels.create` | Kullanıcı işlemi |
| `labels.get` | Kullanıcı işlemi |
| `labels.withProductLabels` | Kullanıcı işlemi |
| `questions.filter` | Kullanıcı işlemi |
| `questions.get` | Kullanıcı işlemi |
| `questions.answer` | Kullanıcı işlemi |
| `finance.settlements` | Kullanıcı işlemi |
| `finance.otherFinancials` | Kullanıcı işlemi |
| `finance.paymentOrders` | Kullanıcı işlemi |
| `finance.cargoInvoiceItems` | Kullanıcı işlemi |
| `webhooks.list` | Bildirim altyapısı — etkinleştirilmedi |
| `webhooks.create` | Bildirim altyapısı — etkinleştirilmedi |
| `webhooks.update` | Bildirim altyapısı — etkinleştirilmedi |
| `webhooks.delete` | Bildirim altyapısı — etkinleştirilmedi |
| `webhooks.activate` | Bildirim altyapısı — etkinleştirilmedi |
| `webhooks.deactivate` | Bildirim altyapısı — etkinleştirilmedi |
| `addresses.seller` | Kullanıcı işlemi |
| `addresses.countries` | Kullanıcı işlemi |
| `addresses.countryCities` | Kullanıcı işlemi |
| `addresses.countryDistricts` | Kullanıcı işlemi |
| `addresses.domesticCities` | Kullanıcı işlemi |
| `addresses.domesticDistricts` | Kullanıcı işlemi |
| `addresses.neighborhoods` | Kullanıcı işlemi |
| `videos.list` | Kullanıcı işlemi |
| `videos.create` | Kullanıcı işlemi |
| `test.createOrder` | Test altyapısı — etkinleştirilmedi |
| `test.updatePackageStatus` | Test altyapısı — etkinleştirilmedi |
| `test.claimsWaitingInAction` | Test altyapısı — etkinleştirilmedi |
| `test.createQuestion` | Test altyapısı — etkinleştirilmedi |
| `test.ipWhitelists` | Test altyapısı — etkinleştirilmedi |
| `test.addIpWhitelist` | Test altyapısı — etkinleştirilmedi |
| `test.deleteIpWhitelist` | Test altyapısı — etkinleştirilmedi |

## Kontrol komutları

- node tests/frontend-checks.cjs
- node tests/trendyol-workflows-checks.cjs

Ürün oluşturma ve kısmi güncelleme alanları için resmi kaynaklar:
- https://developers.trendyol.com/docs/ürün-yaratma-v2
- https://developers.trendyol.com/docs/ürün-güncelleme-onaylı-ürün-v2
- https://developers.trendyol.com/docs/ürün-güncelleme-onaysız-ürün-v2

## Çalışan demo mağaza senaryoları — 27 Ekim 2026

Trendyol Merkezi'nde ana demo mağaza ve Outlet demo mağazası vardır. Her mağaza ayrı örnek stoklarla çalışır: 8 tekstil ürünü/varyantı, 6 sipariş, 2 iade, 3 müşteri sorusu ve finans kayıtları.

Deneme sırası:
1. Ürünler > Detay: özellikler, varyantlar, red nedeni ve mağaza stokları.
2. Stok ve fiyat > Düzenle: değişikliği kaydet; İşlem geçmişinde bekliyor/tamamlandı durumunu izle.
3. Siparişler > Detay: hazırlama, demo fatura, demo kargo ve teslimat adımlarını ilerlet.
4. Müşteri soruları > Yanıtla; İadeler > İncele ve onayla.
5. İşlem geçmişi > örnek başarısız işlemi yeniden dene.
6. Fatura ve garanti / Etiketler: geçersiz örnek belgeleri önizle.

Mağaza kayıtları `Data/integration-connections.json`, demo stok/sipariş/işlem verileri `Data/integration-demo.json` dosyasında saklanır. Bu dizin Git dışında kalır. JSON kalıcılığı, üretim veritabanı veya çok kullanıcılı işlem altyapısı değildir. Demo işlemleri ayrı uçlardan yürür; gerçek mağazaya uygulanamaz. Bekleyen demo işler sonraki okuma sırasında tamamlanır; kesintisiz çalışan bir kuyruk işçisi değildir. Örnek belgeler gerçek fatura veya kargo etiketi olarak kullanılamaz.

Gerçek mağaza bağlantısı, ayrı satıcı kimlik profilleri, kimlik doğrulama/yetkilendirme, kalıcı üretim iş kuyruğu ve gerçek uçtan uca testler hâlâ tamamlanmalıdır.

Kontrol: `dotnet run --project tests/MarketPilot.DemoChecks`.