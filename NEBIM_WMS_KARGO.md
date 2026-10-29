# Nebim V3, mağaza, WMS ve kargo eşitlemesi

Senkronizasyon Merkezi: http://localhost:5080/#sync

Ürün oluşturma uygulamada kapalıdır. Ürün kartı, SKU, barkod, fiyat ve HTTPS görsel bağlantıları Nebim V3 kaynağından gelir. Mağazadan getir işlemi mevcut kaynak ürünlerini eşleştirir. Hedef mağazada ERP ürün kartı yayınlamak, uygulamada ürün oluşturmakla aynı işlem değildir.

## Operatör kullanımı

1. Stok sahibini Nebim V3 veya WMS olarak seçin; depo ve fiyat listesi kodlarını girin.
2. Hedef mağazaları seçin. Her mağaza için stok, fiyat, görsel, ERP ürün kartı ve sipariş çekme yönlerini ayrı işaretleyin.
3. Her görev için saniye cinsinden çalışma aralığını belirleyin (30–86400). Sipariş çekme aralığı mağaza siparişlerini okuma sıklığıdır; tarama penceresi geçmişte kaç günlük siparişin kontrol edileceğidir.
4. Planı kaydedin. Otomatik çalışma için zamanlanmış eşitlemeyi açın. Tek bir görevi Çalıştır ile veya plana dahil görevleri Verileri eşitle ile manuel tetikleyebilirsiniz.
5. Görev durumu tarama/iş üretimini, kalıcı aktarım kuyruğu ise dış servisin onayını gösterir. Bağlantı yapılandırılmış olması başarılı aktarım anlamına gelmez.

Siparişler mağazalardan alınır, ERP ve WMS'ye kalıcı kuyruk üzerinden aktarılır. WMS paket barkodlarını ve miktarlarını siparişle doğrulamadan kargo oluşturulmaz. Kargo servisi takip numarası ve HTTPS etiket bağlantısı döndürür; mağaza bildirimi ayrı kuyrukta izlenir. Belirsiz gönderim sonucu aynı işi körlemesine yeniden göndermek yerine makbuz sorgusuyla doğrulanır.

## Gerçek servis bağlantıları

Mevcut EnterpriseBridge, MarketPilot'a ait bir **ara servis sözleşmesidir**; doğrudan Nebim V3 veya bir kargo firmasının yerel API adaptörü değildir. Gerçek kullanım için Nebim sürümüne, WMS ve seçilen kargo firmasına uygun bir ara servis bu sözleşmeyi karşılamalıdır. API adresleri, hesap yetkileri ve sağlayıcı sözleşmeleri olmadan canlı bağlantı doğrulanamaz.

Sunucuda her bağlantı için `Enterprise:Connectors:{bağlantı}:BaseUrl`, `ApiKey` ve gönderim gerekiyorsa `AllowWrites=true` ayarlanır. Bağlantı adları `erp`, `wms`, `cargo` ve `channel-{mağazaId}` biçimindedir. Anahtarlar sunucuda tutulur. HTTPS varsayılandır; `AllowHttp` yalnızca ihtiyaç duyulan yerel test bağlantıları içindir.

Ara servis Bearer kimlik doğrulaması kullanır:

| Yöntem / yol | İçerik |
| --- | --- |
| GET /v1/catalog | Nebim ürünleri: sku, barcode, name, stock, price, currency, images, warehouseCode |
| GET /v1/stock | WMS barkod ve stok miktarları |
| POST /v1/orders | ERP veya WMS sipariş aktarımı |
| GET /v1/dispatches | WMS paket onayı: externalOrderId, storeId, accountSource, packageId, status, lines, updatedAt |
| POST /v1/shipments | WMS onaylı paket için kargo oluşturma |
| GET /v1/shipments | Kargo: externalOrderId, storeId, accountSource, shipmentId, carrier, trackingNumber, status, labelUrl, updatedAt |
| POST /v1/products | Hedef mağazada ERP ürün kartını yayınlama |
| POST /v1/product-images | Hedef mağazaya görsel bağlantıları gönderme |
| POST /v1/shipment-updates | Mağazaya kargo bildirme |
| GET /v1/receipts/{id} | Aktarım sonucunu doğrulama |

GET listeleri `{ items: [...], nextCursor: null }` biçimindedir; isteklerde `warehouseCode`, `priceListCode` ve varsa `cursor` bulunur. POST gövdesi `{ idempotencyKey, payload }` biçimindedir. Ara servis aynı idempotencyKey için işlemi ikinci kez uygulamamalıdır. Makbuz `{ idempotencyKey, state, reference, safeToRetry }` döndürür; state `completed`, `pending`, `not-found` veya `failed` olabilir. completed için dış servis reference gereklidir. Yeniden gönderim yalnızca not-found ve safeToRetry=true ile mümkündür.

ERP ayrıca `/hooks/erp/catalog` üzerinden imzalı kaynak bildirimi gönderebilir. `Enterprise:Erp:InboundKey` sunucuda tanımlanır; `X-ERP-Timestamp` ve `X-ERP-Signature` başlıkları gerekir. İmza, Unix saniyesi + nokta + ham JSON gövdesinin HMAC-SHA256 hex değeridir. Gövde `{ requestId, items }` biçimindedir.

Gerçek mağaza sipariş okuması `Synchronization:IncludeRealStores`, stok/fiyat yazması `Synchronization:AllowRealWrites` ve ilgili mağaza hesap izinlerine bağlıdır. Görsel, ürün kartı ve kargo bildirimi ayrıca mağazaya özel ara servis gerektirir. Demo verilerinin dış servise gönderimi varsayılan kapalıdır.

## Doğrulama

`GET /api/sync/enterprise/readiness` dış servislere istek göndermeden seçili hedef, depo, fiyat listesi, ERP/WMS/kargo görev bağlantıları ve mağaza kimlik/yazma izinlerini kontrol eder. Hesap kurulmuşsa yalnızca yönetici erişir. Yanıt anahtar içermez; `liveVerified=false` her zaman canlı doğrulama yapılmadığını belirtir. Demo hedefler canlı hazır sayılmaz. Bu uç, eksik yapılandırmaları bağlantılar kurulmadan kontrol etmek içindir.

`dotnet build src/MarketPilot.Web` ve `node tests/enterprise-sync-http-checks.cjs`.

HTTP ve tarayıcı kontrolü sahte ara servisle çalışır; canlı Nebim, WMS veya kargo hesabı doğrulaması değildir.
