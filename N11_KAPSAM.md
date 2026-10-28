# n11 Merkezi — 27 Ekim 2026

n11'in güncel resmî satıcı belgelerine göre ayrı bir adaptör ve işletme ekranı eklendi. Ürün/sipariş işlemleri REST, iade listeleme SOAP, işlem sonucu sorgusu ise salt okuma amaçlı bir REST POST kullanır. Diğer pazaryerlerinin kimlik doğrulama ve servis yapısı n11'e kopyalanmadı.

## Tamamlananlar

13 bölüm: Genel bakış, Ürünler, Siparişler, Stok ve fiyat, n11depom, Kargo, İadeler, Finans, Marka ve kategori, Fatura ve garanti, Etiketler, Adresler, İşlem geçmişi.

- Mağaza ekleme/seçme; demo ve gerçekten bağlı mağazalar ayrı sayılır.
- SKU/barkod kopyalama, ürün açıklaması/özellikleri, varyant örnekleri, komisyon/KDV, kargo hazırlık süresi ve ret nedenleri.
- Paket, kargo barkodu, taşıyıcı takip numarası ve paket geçmişi birbirinden ayrıdır.
- Sipariş tutarı ile mağazanın fatura edeceği tutar ayrı gösterilir; null paket numarası ve eksik TCKN olağan durum olarak ele alınır. TCKN istemci DTO'suna alınmaz.
- Satıcı gönderimi / n11depom ayrımı; depodan gönderilen demo stoğunun satıcı gibi değiştirilmesi ve siparişinin satıcı gibi ilerletilmesi sunucuda reddedilir.
- Demo stok/fiyat, sipariş hazırlama, örnek fatura, iade onayı, kalıcı işlem kuyruğu ve yeniden deneme.
- Örnek paket kartları ve mağaza adresleri demo olarak etiketlidir; gerçek taşıyıcı etiketi veya n11depom adresi değildir.

## Gerçek hesap için yazılan okumalar

| Alan | Resmî servis | Kapsam |
| --- | --- | --- |
| Ürünler / stok-fiyat | `GET https://api.n11.com/ms/product-query` | Tam stockCode, model veya ürün ID; onay, satış durumu, marka, kategori ve sender filtreleri |
| Bağlantı testi | Aynı ürün sorgusu, size=1 | Anahtar yetkisini okuma isteğiyle kontrol eder; boş mağazada sellerId uydurulmaz |
| Siparişler / kargo | `GET /rest/delivery/v1/shipmentPackages` | Sipariş numarası, durum, tarih ve sender; güncelleme tarihine göre sıralama |
| İadeler | SOAP `ClaimReturnListRequest` | Resmî WSDL adresi `https://api.n11.com/ws/ReturnService.wsdl`; servis adresi WSDL'den doğrulanan `/ws/returnService/` |
| Kategoriler | `GET /cdn/categories` | Tam ağaç sunucuda 15 dakika önbelleğe alınır; yalnız aranan yapraklar sayfalanarak tarayıcıya gider |
| İşlem sonuçları | `POST /ms/product/task-details/page-query` | Gönderim yapmaz; daha önce alınan taskId'nin SKU bazında sonuçlarını okur |
| Finans görünümü | Sipariş servisindeki kalem tutarları | sellerInvoiceAmount ve commissionRate - sellerCampaignCommissionRate; hakediş/mutabakat servisi değildir |

Bu okumalar gerçek hesap/SIT testi değildir. Resmî örneklerden hazırlanmış yerel HTTP yanıtlarıyla doğrulanmıştır. Üretim hesabındaki rol/izin ve n11depom uygunluğu ayrıca test edilmelidir.

## n11'e özgü kurallar

- REST çağrılarında `appKey`/`appSecret` başlıkları; SOAP çağrısında güvenle XML olarak kodlanan auth bloğu kullanılır. Basic Auth/LWA kullanılmaz.
- Doğrulanmış ayrı n11 sandbox hostu bulunmadığı için uydurulmadı. Gerçek stage bağlantısı kapalıdır; denemeler yerel demo ile yapılır.
- Ürün sorgusunda sender=`N11`, sipariş sorgusunda belgede gösterildiği gibi sender=`n11` gönderilir; ekranda tek gönderici seçimi bulunur. ALL açıkça gönderilir.
- Sipariş REST servisinin geniş tarih aralıklarını sessizce 15 güne daraltması nedeniyle arayüz/sunucu 15 günden uzun aralığı reddeder. İadelerde de bu uygulama aynı yerel sınırı kullanır.
- Ürün/sipariş sayfaları sabit 25 kayıt; iade SOAP servisi native 20 kayıtla çalışır. Sayfa boyutu ve stok/fiyat alt-üst sınır kontrolü gösterilmez.
- Ürünler tam satıcı SKU/model/ürün numarasıyla aranır. Sağlayıcının desteklemediği barkod araması tüm kataloğu tarıyormuş gibi uygulanmaz; mağaza stok karşılaştırması tam SKU kullanır.
- Sipariş numarası ve ürün ID'si metin olarak korunur; basamak sayısı varsayılmaz.
- Mağaza fatura toplamı yalnız tüm kalemlerin sellerInvoiceAmount değeri bildirilmişse hesaplanır; eksik değer sıfır sayılmaz.
- n11 stok/fiyat yazması asenkron task üretir. İsteğin kuyruğa alınması güncellemenin başarıyla tamamlandığı anlamına gelmez; işlem sonucu ekranı bu ayrımı korur.

## Tamamlanmayan gerçek işlemler

Ürün oluşturma/güncelleme/silme; stok-fiyat gönderimi; kalıcı dış görev takibi ve senkronizasyon; kategori özellik şeması/eşleştirme; sipariş onayı/paket bölme/iptal; toplama talebi; gerçek kargo etiketi; iade kabul/ret/erteleme; fatura linki gönderme/n11faturam; finans hakedişi/mutabakat; mağaza adresleri; mikro ihracat yazmaları tamamlanmadı. Bu düğmeler gerçek mağazada çalışıyormuş gibi sunulmaz.

## Güvenli yapılandırma

Sunucuda `Marketplaces:N11:production:AppKey` ve `AppSecret` gerekir. Anahtarlar mağaza formunda, sohbette veya Git'te paylaşılmaz. Aynı ortam için tek sunucu hesabı vardır; farklı gerçek mağaza adları farklı kimlik bilgisi oluşturmaz.

Sabit api.n11.com HTTPS hostu ve işlem yolu listesi, yönlendirme kapatma, 45 saniye süre ve 16 MB yanıt sınırı kullanılır. SOAP XML DTD/harici varlık çözümlemesi kapalıdır. Ham sağlayıcı hataları/anahtarlar kullanıcıya dönmez. Yeni GET uçları mevcut yerel oturum ve rol korumasını kullanır. Salt okuma SOAP/task POST dışında gerçek pazaryeri yazması yoktur.

## Testler

- `dotnet run --project tests/MarketPilot.N11Checks -p:BuildProjectReferences=false`: başlık auth, sayfalama, uzun kimlikler, sender, fatura tutarları, SOAP okuma/XML kaçışı/DTD reddi, task okuma ve kategori önbelleği.
- `node tests/n11-http-checks.cjs`: geçici veri kökü ve ayrı port; 13 bölüm, filtreler, demo işlemler, depom koruması, mağaza izolasyonu, roller ve mobil görünüm. Ana kullanıcı/verileri değiştirmez.
- Ortak stok/filtre, ön yüz ve mevcut pazaryeri regresyon kontrolleri.

## Resmî kaynaklar

- [Satıcı ürün sorgulama](https://developer.n11.com/documentation/n11-marketplace-entegrasyonu/satici-urun-sorgulama/)
- [Sipariş listeleme](https://developer.n11.com/documentation/n11-siparis-entegrasyonu/siparis-listeleme-servisi/)
- [İade talepleri](https://developer.n11.com/documentation/iade-entgrasyonu/iade-talepleri-servisi/)
- [Kategori ağacı](https://developer.n11.com/documentation/n11-marketplace-entegrasyonu/kategori-agaci-listeleme/)
- [Task sonuç sorgusu](https://developer.n11.com/documentation/n11-marketplace-entegrasyonu/task-detail-sorgulama/)
- [Stok/fiyat gönderim sözleşmesi](https://developer.n11.com/documentation/n11-marketplace-entegrasyonu/urun-fiyat-stok-guncelleme/)
- [n11depom](https://developer.n11.com/documentation/n11depom/n11depom/)
- [Değişiklik günlüğü](https://developer.n11.com/documentation/changelog/)

## Mağaza kimliği ve stok/fiyat yazma güncellemesi

Mağazaya ayrı sunucu kimlik profili seçimi ve kalıcı stok/fiyat gönderimi eklendi. Bu belgedeki önceki salt okuma sınırları stok/fiyat için güncellenmiştir; diğer yazma işlemleri kapsam dışıdır. Güncel sözleşme, izin ve test sınırları: [MAGAZA_HESAPLARI_VE_YAZMA.md](MAGAZA_HESAPLARI_VE_YAZMA.md).
