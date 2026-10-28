# Hepsiburada Merkezi — 27 Ekim 2026

Hepsiburada için ayrı bir işletme ekranı eklendi. Ekran API yolu, JSON veya geliştirici işlem anahtarı göstermez. Müşteri soruları ve videolar sekmeleri eklenmedi. Veritabanı ve gerçek satıcı hesabı kurulmadı.

## Çalışan yerel demo

- 8 tekstil ürünü, 6 sipariş, 2 iade ve örnek finans hareketleri.
- Mağaza ekleme/seçme; bağlı gerçek mağaza ve demo mağaza sayıları ayrı gösterilir.
- Ürünler, stok ve fiyat, siparişler, kargo/paketler, faturalar, iadeler, finans ve işlem geçmişi.
- SKU/barkod kopyalama, ürün detayı ve kanallara göre stok karşılaştırması.
- Sunucuda uygulanan arama/durum/barkod filtreleri; sabit 25 kayıtlık sayfalar. Stok/fiyat alt-üst sınırı veya sayfa boyutu kontrolü yok.
- Stok/fiyat düzenleme, sipariş ilerletme, örnek fatura/sevk fişi, iade onayıyla stok ekleme ve başarısız işlemi yeniden deneme.
- Komutlar mevcut kalıcı demo kuyruğuna yazılır. Mağaza/pazaryeri verileri birbirinden ayrılır; tarayıcı kapalı olsa da sunucu çalışan kuyruğu işler.
- Demo faturalar ve sevk fişleri geçersiz örnek olarak etiketlidir. HepsiJet adı örnek kayıttır; taşıyıcı bağlantısı veya geçerli kargo etiketi anlamına gelmez.

## Gerçek bağlantıda yazılmış okuma desteği

| İş | Resmî servis | Uygulama durumu |
| --- | --- | --- |
| Bağlantı testi | Listing listesinden tek kayıt okuma | HTTP fixture ile test edildi; gerçek hesapla denenmedi |
| Ürün/stok/fiyat | `GET /listings/merchantid/{merchantId}` | Native offset/limit, tam merchantSku ve satış durumu |
| Paketlenecek sipariş kalemleri | `GET /orders/merchantid/{merchantId}` | Kalem bazında ayrı satırlar; offset/limit ve tarih aralığı |
| Ödeme bekleyen kalemler | `GET /orders/merchantid/{merchantId}/paymentawaiting` | Ayrı durum seçimi |
| Paketler | `GET /packages/merchantid/{merchantId}` | Kargo sekmesinin varsayılan kaynağı |
| Sipariş numarasıyla sorgulama | `GET /orders/merchantid/{merchantId}/ordernumber/{orderNumber}` | Tam sipariş numarası |
| Faturası eksik paketler | `GET /packages/merchantid/{merchantId}/missing-invoice` | Faturalar sekmesi; fatura kesme değildir |
| İade/talepler | `GET /claims/merchantId/{merchantId}/status/{status}` | Durum + offset/limit; normalize liste |

Bu destek bütün Hepsiburada API'lerinin tamamlandığı anlamına gelmez. Listing yanıtında eksik olan ürün adı, barkod, görsel, kategori ve onay bilgileri uydurulmaz. Listing, katalog başvuru onayıyla aynı süreç değildir. Siparişlerin ödenen/ödenmeyen servisleri kalem sayfalar; tüm siparişleri birleştirmiş gibi toplam gösterilmez. Gerçek ürün stok karşılaştırması tam satıcı SKU'sunu kullanır; barkodu bulunmayan ürünlerde barkod eşleşmesi iddia edilmez.

### Henüz tamamlanmayan gerçek işlemler

- Ürün oluşturma/güncelleme, kategori/özellik eşleştirme ve katalog başvuru takibi.
- Stok/fiyat gönderimi ve asenkron gönderim sonuçlarının izlenmesi.
- Paket oluşturma/bozma, kısmi paketleme, kargo değiştirme, geçerli etiket/PDF/ZPL alma ve iptal yazmaları.
- İade kabul/red/değişim yazmaları.
- Muhasebe/ödeme/komisyon/mutabakat servisi; gerçek finans ekranı bunu açıkça belirtir.
- Fatura yükleme ve E-Faturam bağlantısı. Fatura durumu okumak fatura üretmez.
- Kargo teslimat olayları, webhook doğrulama ve sürekli gerçek senkronizasyon.
- Gerçek satıcıyla SIT sözleşme testi; ardından ayrı üretim testi.

Gerçek yazma uçları bu sürümde sunulmaz. Demo düğmeleri gerçek mağazada görünmez. Hesap bağlamak tek başına yukarıdaki eksik işlemleri tamamlamaz.

## Güvenli yapılandırma (arka taraf)

Her ortam için `Marketplaces:Hepsiburada:{stage|production}` altında `MerchantId` (GUID), `Username`, `Password`, `UserAgent` gerekir. Güncel servis anahtarı/entegratör tanımı Hepsiburada satıcı hesabından alınmalıdır; değerler sohbete, tarayıcı formuna veya Git'e yazılmamalıdır. Test ve üretim ayrı yapılandırılır. Aynı ortamda bir sunucu hesabı kullanılır; farklı gerçek mağaza adları farklı kimlik bilgileri oluşturmaz.

Sunucu Basic Auth + User-Agent ekler; yalnız sabit listing/OMS HTTPS hostlarına gider, yönlendirme izlemez. Yanıt 10 MB, süre 45 saniye ile sınırlandırılır. Yetki, oran sınırı ve ağ hataları ham sağlayıcı yanıtı veya anahtar içermeyen mesajlara çevrilir. Otomatik gerçek yazma/tekrar deneme yapılmaz. Mevcut yerel erişim/rol koruması yeni GET uçlarında da geçerlidir.

## Doğrulama

- `dotnet run --project tests/MarketPilot.HepsiburadaChecks -p:BuildProjectReferences=false`: gerçek ağa çıkmayan HTTP fixture testleri.
- `node tests/hepsiburada-http-checks.cjs`: geçici içerik kökü ve ayrı portta tarayıcı/HTTP testi; ana verileri değiştirmez. Chrome ve mevcut yerel Playwright kurulumunu kullanır.
- Mevcut Trendyol, demo, ortak stok, filtre ve ön yüz kontrolleri.

## İncelenen resmî kaynaklar

- [Listing bilgilerini sorgulama](https://developers.hepsiburada.com/tr/companies/hepsiburada?category=baslangic&op=Listing+Bilgilerini+Sorgulama&product=listeleme&version=v1&view=endpoint)
- [Sipariş entegrasyonu rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?category=baslangic&guide=siparis-entegrasyonu-onemli-bilgiler&product=siparis-olusturma-entegrasyonu&version=v1.0&view=guide)
- [Sipariş servisi değişiklikleri: paketler/faturasız kayıtlar](https://developers.hepsiburada.com/tr/changelogs/hepsiburada-entegrasyon-eylul-2025)
- [Sipariş detay sorgusu](https://developers.hepsiburada.com/hepsiburada/reference/get_orders-merchantid-merchantid-ordernumber-ordernumber)
- [Duruma göre talepler](https://developers.hepsiburada.com/tr/companies/hepsiburada?category=hepsiburada-talep-entegrasyonu&op=Get__claims_merchantId_merchantId_status_status&product=talep-listeleme&version=v1.0&view=endpoint)
- [Talep rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=talep-onemli-bilgiler-2&product=talep-entegrasyonu&view=guide)
- [Katalog rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=katalog-onemli-bilgiler&product=katalog-urun-entegrasyonu&view=guide)
- [Ürün güncelleme rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?category=baslangic&guide=urun-guncelleme-onemli-bilgiler&product=urun-guncelleme-entegrasyonu&version=v1.0&view=guide)

Dokümanların herkese açık indekslenen alanları incelendi. Hesapsız erişimde bazı yeni portal sayfalarının tam şeması alınamadığı için doğrulanamayan yazma sözleşmeleri tahmin edilerek uygulanmadı. Canlı sözleşme uyumluluğu gerçek hesap testine kadar kesinleşmiş sayılmaz.

- [Servis anahtarı oluşturma/görüntüleme](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=entegratore-servis-anahtari-eklemegoruntuleme&product=api-authentication&view=guide)

## Ayrıntılı ekranlar — 27 Ekim 2026

Ürün kartlarında açıklama, görsel/ürün simgesi, fiziksel/satılabilir/rezerve stok ayrımı, kumaş/renk/beden, aynı modelin varyantları ve kayıtlı değişiklikler gösterilir. Sipariş detayı beş aşamalı ilerleme, teslimat adresi, kalem tutarları, fatura ve kargo bilgileriyle işlem geçmişini içerir. Kargo etiketleri, kategori/özellikler ve mağaza adresleri için etiketli demo görünümleri eklendi. Finans özeti yalnız görüntülenen sayfanın hareketlerini toplar; kâr/hakediş olarak sunulmaz. İade stok etkisi ve iş ayrıntıları açılabilir. Canlı etiket/kategori/adres bağlantıları tamamlanmadığından gerçek mağazada örnek veri gösterilmez.

## Mağaza kimliği ve stok/fiyat yazma güncellemesi

Mağazaya ayrı sunucu kimlik profili seçimi ve kalıcı stok/fiyat gönderimi eklendi. Bu belgedeki önceki salt okuma sınırları stok/fiyat için güncellenmiştir; diğer yazma işlemleri kapsam dışıdır. Güncel sözleşme, izin ve test sınırları: [MAGAZA_HESAPLARI_VE_YAZMA.md](MAGAZA_HESAPLARI_VE_YAZMA.md).
