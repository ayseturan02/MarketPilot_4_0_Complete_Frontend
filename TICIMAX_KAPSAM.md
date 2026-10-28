# Ticimax merkezi — 27 Ekim 2026

Ticimax, diğer pazaryeri merkezlerinin yanında ayrı bir satış kanalı olarak eklendi. Hesap bağlanmadı; gerçek mağazada doğrulama henüz yapılmadı.

## Ekranlar ve çalışan demo

12 bölüm: Genel bakış, Ürünler, Siparişler, Stok ve fiyat, Kargo, İadeler, Finans, Marka ve kategori, Fatura ve garanti, Etiketler, Adresler, İşlem geçmişi.

- 7 ürün kartı / 8 varyant, 6 sipariş, ödeme kayıtları ve örnek kargo paketleri.
- Kart detayı, varyant özellikleri, SKU/barkod kopyalama ve diğer mağazalardaki stokları inceleme.
- Tam ürün kartı, SKU veya barkod araması; yayın durumu; sipariş numarası, durum ve tarih filtreleri. Ürün ve siparişlerde yerel filtreleme için tüm katalog indirilmez.
- Demo varyant stok/fiyat düzenleme ve sipariş ilerletme; işlem sırası ve yerel dosya kalıcılığı. Diğer mağazaların demo verileri ayrıdır.
- Demo belgeleri mali belge veya taşıyıcı etiketi değildir.

## Gerçek okuma altyapısı

Mağazanın kendi HTTPS alan adındaki UrunServis ve SiparisServis SOAP servisleri kullanılır. İşlem ve alan adları mağazanın WSDL/XSD tanımından okunur. Bilinmeyen zorunlu alan, desteklenmeyen sayfalama veya arama filtresi varsa işlem durdurulur. SKU/barkod filtresi eski dokümanda garanti edilmediğinden desteklenmeyen mağazada kart numarasıyla arama gerekir.

Uygulanan okuma işlemleri: SelectUrun, SelectSiparis, SelectSiparisOdeme, SelectSiparisUrun, SelectSiparisKargoPaket, SelectKategori, SelectMarka, SelectParaBirimi ve SelectTedarikci. Ürün/sipariş listeleri 25 kayıtlık doğal sayfalama kullanır. Sayı servisi çağrılmadığından sayfa doluysa sonraki sayfa olabileceği gösterilir; son sayfa boş olabilir. Kategori/marka/para birimi/tedarikçi servisleri doğal olarak tam liste döndürür; bunlar ekranda bölünür. İlgili ödeme, ürün ve kargo okumalarında sipariş seçmek zorunludur.

Ondalıklı varyant stokları korunur. Vergi dahil/hariç bilgisi ayrıca gösterilir; bilinmeyen para birimi TL kabul edilmez. Gerçek varyant fiyatının döviz kodu, para birimi listesi yüklenince eşleştirilir. Mağazalar arası ortak stok görünümünde normalize edilmemiş gerçek fiyatlar boş bırakılır.

Üretim ayarları sunucuda `Marketplaces__Ticimax__production__BaseUrl` ve `Marketplaces__Ticimax__production__UyeKodu` olarak tanımlanacak. Form anahtar saklamaz. Aynı ortamda eklenen mağaza adları aynı sunucu hesabını kullanır; farklı adlar bağımsız hesap oluşturmaz. Stage ortamı gerçek bağlantı için desteklenmiyor.

## Kalanlar

- Gerçek mağaza alan adı, servis yetkisi ve o mağazanın WSDL sözleşmesiyle uçtan uca doğrulama.
- Gerçek ürün/stok/fiyat yazma, sipariş durumu değiştirme ve otomatik senkronizasyon açık değil.
- İadeler, siparişin iade durumları üzerinden gösterilir; bağımsız iade talebi/onay sistemi uygulanmadı.
- Finans sipariş ödeme kayıtlarını gösterir; banka mutabakatı/kârlılık sistemi değildir.
- Adresler sipariş teslimat adresleridir. Gerçek depo adresi yönetimi, fatura/garanti dosyası indirme ve geçerli PDF/ZPL kargo etiketi henüz yok.
- Kalıcı veritabanı bu çalışmada kurulmadı; mevcut yerel dosya saklaması kullanılıyor.

## Kontroller

`dotnet run --project tests/MarketPilot.TicimaxChecks`: WSDL ad alanları/sıralaması, XML kaçışları, sayfalama, desteklenmeyen aramanın tam katalog çağrısı yapmaması, ondalık stok/döviz/vergi, yazma kapalı olması, özel ağ ve DTD engeli. Servis yanıtları test örneğidir; gerçek hesaba çağrı yapılmaz.

`node tests/ticimax-http-checks.cjs`: geçici veri dizininde 12 sekme, filtreler, ilişkili kayıtlar, stok ve sipariş demo akışı, mağaza ekleme/izolasyonu, rol kontrolleri ve mobil genişlik. Ana demo kayıtları değiştirilmez.

## Resmî kaynaklar

- https://www.ticimax.com/web-servis-api/
- https://static.ticimax.com/dokumanlar/webservis.pdf (ürün servisi, v1.4)
- https://static.ticimax.com/dokumanlar/siparisservis.pdf (sipariş, ödeme ve kargo paketleri)

PDF belgeleri ile mağazanın etkin sözleşmesi farklı olabilir; gerçek hesap testi yapılana kadar canlı uyumluluk garantisi verilmez.

## Mağaza kimliği ve stok/fiyat yazma güncellemesi

Mağazaya ayrı sunucu kimlik profili seçimi ve kalıcı stok/fiyat gönderimi eklendi. Bu belgedeki önceki salt okuma sınırları stok/fiyat için güncellenmiştir; diğer yazma işlemleri kapsam dışıdır. Güncel sözleşme, izin ve test sınırları: [MAGAZA_HESAPLARI_VE_YAZMA.md](MAGAZA_HESAPLARI_VE_YAZMA.md).
