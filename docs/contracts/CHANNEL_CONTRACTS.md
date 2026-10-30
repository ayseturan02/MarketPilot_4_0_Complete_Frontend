# Pazaryerleri ile ortak ERP/WMS sözleşmesi

`ChannelContracts` altı mevcut adaptörün ortak okuma yüzeyini tanımlar. Yeni native endpoint, ürün oluşturma isteği veya kimlik doğrulama yöntemi eklemez. Capability matrisi `GET /api/sync/enterprise/channel-contracts` üzerinden okunur. Matris, MarketPilot'ın uyguladığı işlemleri belirtir; sağlayıcının tüm API yeteneklerine ilişkin bir iddia değildir.

| Kanal | Korunan kimlikler | Sayfalama / tarih | Özel kontrol |
|---|---|---|---|
| Trendyol | Ürün/content/main ID, SKU, barkod, paket | Sayfa, ürün token, sipariş cursor, tarih çifti | Batch kabulü tamamlanma sayılmaz |
| Hepsiburada | İlan/HB SKU, merchant SKU, barkod, paket/sipariş | Offset/sayfa, mevcut tarih doğrulaması | Eksik KDV bilinmiyor kalır |
| n11 | Ürün ID, satıcı SKU, paket/sipariş | Sayfa ve tarih çifti | n11depom stoğuna yazılmaz |
| Amazon | ASIN/content, satıcı SKU, sipariş ve sağlanan paketler | NextToken/cursor, mevcut tarih sınırları | FBA/AFN/AMAZON stokları korunur |
| Boyner | Ürün/content, SKU, barkod, paket | Sayfa ve tarih çifti | Operasyon kabulü işlenme sayılmaz |
| Ticimax | Kart/varyant ID, SKU, barkod, sipariş | Sayfa token, mevcut SOAP tarih filtresi | Eksik paket, fiyat veya para birimi üretilmez |

`ReadProductsAsync` ve `ReadOrdersAsync` mevcut adaptöre sorguyu değiştirmeden iletir; toplam, sayfa, devam token'ı, cursor ve `HasMore` korunur. Adaptörün desteklemediği filtre için kendi reddi korunur. Hesap ve tenant kimliği okuma çağrısından projeksiyona taşınır; kaynak ürün ana kimliği ve liste fiyatı kaybolmaz.

ERP bağlantısı yalnız açık SKU+barkod ve tekil ERP ürün/varyant koduyla hazırlanır. Ada göre eşleştirme yapılmaz. Eksik veya çoklu eşleşme incelemeye gider. Yeni ürün oluşturma her altı kanalda `501 unsupported_operation` döndürür. Hazırlığın tamamlanması herhangi bir yayın isteği gönderildiği anlamına gelmez.

Ham sipariş/paket durumu korunur; tanınmayan durum için canonical değer `null` kalır. Negatif/geçersiz tutar, stok, KDV, eksik veya tekrarlı kalem kimliği inceleme nedeni üretir. Kampanya, indirim, takip ve kaynak sipariş bilgisi kaynak modelde korunur. Vergi veya kur hesabı tahmin edilmez.

Makbuzda `Accepted` ve `Processed` ayrıdır. Tamamlanma için açık işlem kanıtı ve referans gerekir. Native hata kodu ve HTTP durumu korunur; gizli ham hata gövdesi ortak hata modeline alınmaz. 408/429/500/502/503/504 tekrar değerlendirilebilir; 501 desteklenmeyen işlemdir ve otomatik tekrar edilmez.

Doğrulama: `npm.cmd run check:channels`. Loopback sahte HTTP sunucusu altı mapper için ürün/sipariş fixture'ları, sayfalama, cursor, tarih, hata, bilinmeyen alan, kanal stoğu ve desteklenmeyen işlem kontrollerini çalıştırır. Mevcut native transport fixture testleri `MarketPilot.IntegrationChecks`, `HepsiburadaChecks`, `N11Checks`, `AmazonChecks`, `BoynerChecks` ve `TicimaxChecks` projelerindedir. Bu testler canlı API çağrısı yapmaz.
