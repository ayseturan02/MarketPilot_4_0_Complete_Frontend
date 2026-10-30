# Bağımsız WMS sağlayıcı sözleşmesi

WMS regresyon paketini çalıştırmak için: `npm.cmd run check:wms`. Çok depolu stok HTTP/tarayıcı kontrolleri: `node tests/wms-inventory-http-checks.cjs`.

`src/MarketPilot.Web/Wms` altında bulunan `MarketPilot.Web.Wms.IWmsProvider`, mevcut özel `Enterprise.IWmsProvider` bridge arayüzünden bağımsızdır. Nebim V3 ile ilişkili bir WMS'ye veya ayrı bir sağlayıcıya bağlanabilmesi için MarketPilot içi ortak modeller ve işlemler sunar. Gerçek WMS URL'si, protokolü, kimlik doğrulaması veya payload formatı varsayılmaz.

## Sözleşme

`WmsConnectionProfile` işletme/profil kimliğini, sağlayıcı anahtarını, modu ve etkinlik/yazma izinlerini taşır. `WmsCapabilities` her işlemi ayrı ayrı ilan eder; bir yetenek desteklenmiyorsa mock çağrısı `NotSupported` ile reddedilir. Gerçek adaptör ve gerçek veritabanı eklenmemiştir.

`IWmsProvider` işlemleri:

- Depo listesi ve depo bazında stok anlık görüntüsü okuma
- Depo bazında, isteğe bağlı `since` zamanıyla stok hareketi okuma
- Fulfillment talebi gönderme ve kabul durumunu sorgulama
- Rezervasyon, toplama ve paketleme durumlarını sorgulama
- Sevkiyat durumu, taşıyıcı ve takip bilgisini sorgulama
- İptal veya iade uygunluğunu sorgulama

Tüm çağrılar cancellation token alır, correlation kimliği ve tamamlanma zamanı içeren `WmsResult<T>` döndürür; sağlayıcı hataları `WmsProviderException`/`WmsError` ile açıkça bildirilir.

## Mock ve HTTP yüzeyi

Mock depo/stok/hareket verileri sabittir. Uygulama DI sağlayıcısında fulfillment makbuzları mevcut EnterpriseSync deposunda kalıcı tutulur; yeniden başlatma sonrası aynı external request ID sorgulanabilir. EnterpriseSync verilmeden doğrudan oluşturulan test sağlayıcısı yalnız bellek kullanır. Bu makbuz deposu ikinci bir iş kuyruğu değildir. Mock yazma varsayılan olarak kapalıdır.

Deposu reddedilen talebin kabul sonucu sorgulanabilir; ancak rezervasyon, toplama, paketleme, sevkiyat ve uygunluk sorguları `FulfillmentRejected` (HTTP 409) döndürür. Reddedilen talep için başarılı işlem durumu üretilmez. Kabul edilen talebin satırları çağıranın koleksiyonundan kopyalanır; sonradan yapılan yerel değişiklikler talebi değiştirmez. Gecikme sırasında cancellation oluşursa gönderim kaydedilmez. Yanıt kaybı senaryosu ise `AmbiguousSubmission` ile ayrıca simüle edilir ve kabul sorgusuyla uzlaştırılır.

## WMS çok depolu stok ekranı

`WmsInventoryService`, her etkin depodan WMS stok görüntülerini ve destekleniyorsa hareketleri okur. Son görüntüler, kaynak olay geçmişi, kanal depo eşlemeleri, depo güvenlik stokları ve kanal limitleri işletme `Data/wms-inventory.json` dosyasında saklanır. Gerçek veritabanı kullanılmaz; stok kaynağına yazılmaz.

Onaylı satılabilir stok kuralı: `fiziksel - rezervasyon - hasarlı - bloke - iade kontrolü - ayrılmış - transferde - güvenlik stoğu`; sonuç sıfırın altına inerse sıfır olur. Bileşenlerden biri `null`/bilinmiyorsa satılabilir değer de bilinmiyor kalır. Negatif fiziksel veya kategori stoğu satışa kapatılır. Provider-managed/FBA/n11depom satırları ayrı gösterilir, bu hesaba ve kanal stoğuna katılmaz.

Yeni işletmede kural otomatik onaylanmaz; `RuleApprovedAt` boşken satılabilir miktar bilinmiyor, durum `rule-approval-required` olur. Yetkili kullanıcının “Kuralı onayla ve ayarları kaydet” işlemi onay zamanını kaydeder. Kapalı depolar tahsise alınmaz. Hesap bazlı önizleme depo toplamını ve kanal limitini gösterir; eksik depo/SKU verisi sıfıra çevrilmez. Sürümlü görüntü sürümsüz olayla değiştirilemez. Sağlayıcı yönetimli stok sahipliğini değiştiren olay çelişki olarak tutulur; mevcut stok korunur. ERP kimliği bulunduğu halde stok pozisyonu yoksa `missing-erp-stock` inceleme kaydı gösterilir.

Bir pazaryeri hesabına sıfır, bir veya daha fazla depo atanabilir. Atanmış depolardan herhangi birinde ilgili SKU görüntüsü yoksa kanal miktarı bilinmiyor kalır; eksik stok sıfır varsayılmaz. Kanal üst limiti, hesaplanan depo toplamına uygulanır. Bu ayarlar yalnız görüntü/projeksiyon içindir; pazaryerine stok push'u veya WMS stok yazımı yapmaz.

Kaynak stok sürümü geriye gidemez. Eski olay `stale`, aynı sürümde farklı içerik `conflict` olarak geçmişe eklenir ve güncel stok korunur. Sürüm yoksa kaynak zamanı kullanılır; sıralama yapılamayan çelişki güncel görünümü ezmez. ERP stok pozisyonları ile WMS fiziksel miktarı ayrı kaynak kayıtları olarak karşılaştırılır; uyuşmazlık, birim uyuşmazlığı veya belirsiz ERP eşleşmesi inceleme listesinde gösterilir, otomatik kazanan seçilmez.

Yeni ekran mevcut `inventory` / “Stok & Depolar” görünümüdür:

- `GET /api/wms/inventory`: depolar, kategori bazlı WMS stokları, satılabilir projeksiyon, kaynak zamanı, hareket/geçmiş, ERP farkları ve kanal dağıtım görünümü.
- `GET /api/wms/inventory/movements` ve `/api/wms/inventory/history`: isteğe bağlı depo/SKU filtresiyle geçmiş.
- `PUT /api/wms/inventory/policy`: depo güvenlik stoğu, hesap-depo eşlemesi ve kanal üst limiti. Yalnız yönetici yazabilir. Bu bir stok değişikliği değildir.

Stok değiştiren `/api/wms` endpoint'i eklenmedi. Mevcut `POST /api/stock/movements` yolu da stok kaynağına yazmayı reddetmeye devam eder.

Uygulama yolları `/api/wms` altındadır: `/warehouses`, `/warehouses/{id}/stock`, `/warehouses/{id}/stock-movements`, `/fulfillment-orders` ve talep kimliği altında `acceptance`, `reservation`, `picking`, `packing`, `shipment`, `eligibility` uçları. Bu yollar sağlayıcı/native WMS endpoint'leri değildir.

İsteğe bağlı yerel test senaryoları:

```json
{
  "Wms": {
    "Mode": "mock",
    "Enabled": true,
    "AllowMockWrites": false,
    "Mock": {
      "DelayMilliseconds": 0,
      "FailureOperation": null
    }
  }
}
```

`DelayMilliseconds` 0-30000 aralığında gecikme uygular. `FailureOperation` `warehouses`, `stock`, `stock-movements`, `fulfillment-submission`, `acceptance-status`, `reservations`, `picking-status`, `packing-status`, `shipment-status`, `cancellation-eligibility`, `return-eligibility` veya `*` olabilir; hata `503`/tekrar denenebilir `SimulatedFailure` olarak döner. Konfigürasyon geliştirme/test içindir; gerçek adaptörün capabilities matrisi kendi sağlayıcısı tarafından belirlenmelidir.

`dotnet run --project tests/MarketPilot.WmsChecks` sabit fixture'ları, tüm işlemleri, yetenek reddini, idempotency'yi, gecikme/hata ve cancellation davranışını; çoklu depo ataması/limitini, satılabilir hesabı, sağlayıcı yönetimli stoğu, sürüm gerilemesini, belirsiz/negatif stok engelini, ERP/WMS farklarını ve JSON geçmiş kalıcılığını doğrular. Test gerçek ağa veya veritabanına bağlanmaz.

## Fulfillment orkestrasyonu

Regresyon komutu: `npm.cmd run check:fulfillment`. Mock uçtan uca HTTP/tarayıcı testi, politika onayı öncesi blokajı, ERP önce sırasını, eşzamanlı tekrarlı sipariş alımını, kimlik çatışmasını, iki depo/dört paketi, rezervasyon-toplama-paketleme-sevk adımlarını ve ekranda durum geçmişini doğrular. Birim testleri WMS önce sırasını, kısmi sevki, depo reddini, yetersiz stoğu, yanıt kaybı uzlaştırmasını ve yeniden başlatmayı da kapsar.

Aynı SKU farklı sipariş satırlarında yer alsa da bir depodaki miktar yalnız bir kez tahsis edilir. Rezervasyon yanıtı talep/depo/SKU/birim/miktarla; kabul, toplama, paketleme ve sevkiyat yanıtları dış talep kimliğiyle doğrulanır. Paket satırlarında SKU/birim eşleşmesi, takip numarası ve sevk zamanı zorunludur. Yinelenen paket kimlikleri, değişmiş paket içeriği ve önceki paketlerle birlikte tahsisi aşan toplam miktar incelemeye bloke edilir. Kısmi sevk tam işlenmiş sayılmaz; tekrar sorgulama paket veya durum bildirimi çoğaltmaz.

`WMS Fulfillment` ekranı normalize edilmiş pazaryeri siparişini doğrulayıp işletmenin onaylı `FulfillmentWorkflowPolicy` sırasına göre ERP sipariş kaydı ve WMS sevk talebini ilerletir. Varsayılan politika onaysızdır; depo önceliği, kısmi tahsis izni ve `ERP → WMS`, `WMS → ERP` veya yalnız WMS sırası açıkça belirlenmeden dış işlem yapılmaz. WMS önce seçilirse ERP kaydı WMS kabulü doğrulandıktan sonra ilerler.

- `GET/PUT /api/wms/fulfillment/policy`: işletme politikasını okur/günceller.
- `GET /api/wms/fulfillment/workflows` ve `GET /api/wms/fulfillment/workflows/{id}`: sipariş, depo talepleri, paketler, güncellemeler ve geçmişi döndürür.
- `POST /api/wms/fulfillment/orders`: sipariş alımı. Aynı dış sipariş/idempotency içeriği tekrarlanırsa mevcut workflow döner; farklı içerikle aynı kimlik 409 çatışmasıdır.
- `POST /api/wms/fulfillment/workflows/{id}/advance`: yalnız bir sonraki güvenli adımı çalıştırır veya harici kabul/rezervasyon/toplama/paketleme/sevk durumunu sorgular. `retry-validation` yalnız dış yazma başlamamış workflow'u yeniden doğrular.

Kabul, rezervasyon, toplama, paketleme ve sevk ayrı durumlar olarak tutulur; `accepted` siparişin işlendiği anlamına gelmez. Satılabilir WMS stokuna göre talepler birden fazla depoya bölünebilir; eksik stok, depo reddi, birden fazla paket, kısmi sevk ve takip bilgileri workflow geçmişinde saklanır. Her workflow ve depo talebi kararlı external request ID/idempotency key taşır. Timeout veya belirsiz yanıt aynı kimlikle durum sorgusu gerektirir; yeni talep otomatik gönderilmez. Pazaryeri/ERP durum güncellemeleri mock publisher üzerinden idempotent izlenir.

Workflow/policy geçmişi işletme içerik kökündeki `Data/wms-fulfillment.json` dosyasına atomik yazılır; bu bir veritabanı değildir. Gerçek WMS, ERP veya pazaryeri bağlantısı kurulmaz. Dış yazmalar ancak WMS ve gerekli ERP mock izinleri etkin olduğunda çalışabilir; stok miktarı değiştiren bir işlem yoktur.
