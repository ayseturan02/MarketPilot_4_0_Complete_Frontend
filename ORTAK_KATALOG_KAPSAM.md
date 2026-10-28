# Ortak katalog ve operasyon bağlantıları — 27 Ekim 2026

## Değişen veri akışı

Ürün & Katalog altındaki sekiz ekran artık birbirinden bağımsız tarayıcı taslakları yerine sunucudaki ortak kayıtları kullanır: Tüm Ürünler, Ürün Eşleştirme, Stok & Depolar, Barkod Merkezi, Fiyat & Komisyon, Kategori & Özellikler, İlan & Yayın Yönetimi ve Toplu İşlemler.

Trendyol, Hepsiburada, Amazon, n11, Boyner ve Ticimax aynı mağaza kayıt defterinden okunur. Genel Satış Kanalları ekranı da bu kayıtları gösterir. Pazaryeri ürün satırlarından merkezi ürün detayına, merkezi ürün detayından ilgili mağazaya geçilebilir.

Mevcut yerel ürünler silinmez. İlk geçişte mevcut demo mağazalarındaki sekiz tekstil varyantı ortak kimliklerle bağlanır. Standart boş kurulumda dokuz eski yerel ürün + sekiz tekstil ürünü bulunur. Yedi demo mağaza kaydı (Trendyol Outlet dahil) ayrı stoklarla takip edilir. Sonradan eklenen mağazalar “Mağazadan getir” ile bağlanır.

## Ortak ürün kaydı

- Merkezi ürün, mağaza ürünü ve ürün kartı/varyant kimlikleri ayrıdır. Aynı barkod öncelikli; uygun durumda tam SKU üzerinden eşleştirme yapılır. Ürün adına göre birleştirme yoktur. SKU aynı ama barkod farklıysa aktarım durur.
- Demo ve gerçek ürünler otomatik olarak birleştirilmez. Açık ürün eşleştirmesi yapılabilir; demo/gerçek sınırı korunur.
- Merkezi ürünün adı, SKU, barkod, marka, kategori, açıklama, stok, fiyat, para birimi, KDV bilgisi ve aktif/pasif/arşiv durumu sunucuda saklanır. Gelen görseller ve mevcut özellikler ilk aktarımda korunur.
- Mağaza stokları toplanıp merkezi stok kabul edilmez. Yeni mağaza aktarımı mevcut merkezi değerleri ezmez. Ticimax'in ondalıklı stokları korunur; bilinmeyen fiyat/stok sıfır diye gösterilmez, döviz kodu tahmin edilmez.
- Mağaza kategori/marka numaraları ve özellik eşleştirmeleri kalıcıdır. Kanalın kategori şemasına karşı otomatik uygunluk kontrolü ve gerçek ürün yayını henüz yapılmaz.

## İşlem sırası

Merkezi ürünü düzenle → hedef mağaza kayıtlarını seç → stok/fiyat farklarını önizle → hazır mağaza kayıtlarına uygula → işlem geçmişinden sonucu izle.

İşler sunucuda kalıcı tutulur. Aynı işlem kimliği tekrar gönderildiğinde aynı iş döner; aynı kimlik farklı içerikle kullanılamaz. Ürün sürümü önizlemeden veya sıraya alınmadan sonra değişmişse eski değerlerle gönderim engellenir. İşler uygulama yeniden başladıktan sonra devam eder. Amazon FBA ve n11depom stokları satıcı stok düzenlemesinden korunur. Hedefler farklı sonuçlanırsa ayrıntıda her mağazanın sonucu görülebilir. Başarısız iş için güncel değerlerle yeni önizleme yapılır.

Gerçek stok/fiyat hedefleri artık kalıcı pazaryeri kuyruğuna bağlanır. Mağaza profili, sunucu yazma izni, bilinen para birimi ve KDV dahil fiyat gereklidir. Varsayılan izinler kapalıdır. Ayrıntılar: [MAGAZA_HESAPLARI_VE_YAZMA.md](MAGAZA_HESAPLARI_VE_YAZMA.md).

## Sipariş, gönderi ve ana panel

Genel sipariş listesi mevcut yerel kayıtlarla mağaza demo siparişlerini birlikte gösterir. Mağaza merkezindeki demo durum değişikliği genel listeye yansır. Sipariş satırından eşleşen merkezi ürüne geçilebilir. Takip numarası olan mağaza gönderileri genel gönderi listesinde de görünür. Mağaza sipariş/gönderi işlemleri ilgili merkeze yönlendirilir; yerel durum formu üzerinden yanlış kayda yazılmaz.

Gerçek merkezlerde okunan sipariş sayfaları (ortak okuma yolu ve altı merkezin ilgili sayfaları) ortak sipariş deposuna eklenir/güncellenir. Bir sayfa okundu diye tüm hesap çekilmiş kabul edilmez. Genel gerçek sipariş görünümü yalnızca daha önce okunan kayıtlardan oluşur; zamanlanmış sipariş okuması ve webhook alıcısı artık mevcuttur; zamanlayıcı seçilen kontrol penceresini ve başarılı tarama zamanını kullanır, tüm geçmişin sınırsız aktarıldığı varsayılmaz. Ticimax sipariş yanıtında ürün satırları bulunmadığında merkezi liste bu satırları uydurmaz.

Ana panelin ürün, sipariş ve kanal toplamları ortak kayıtlardan hesaplanır. TRY toplamı farklı para birimlerini dönüştürüp toplamaz. Sipariş tutarı tahsilat/net kâr değildir; iadeler ve finansal mutabakat ayrı geliştirme gerektirir.

## Saklama ve erişim

Yeni bir veritabanı kurulmadı. Ortak katalog, eşleştirmeler, okunan gerçek siparişler ve işler mevcut `Data/demo-store.json` içinde saklanır; mevcut şifreli yedek kapsamına otomatik girer. Dosyalar geçici dosya + değiştirme ile yazılır. Bir işlem hata verirse bellekte kısmi değişiklik bırakılmaz. Okunamayan katalog dosyası sessizce sıfırlanmaz.

Mevcut yerel oturum/rol denetimleri uygulanır. Görüntüleyici kayıt ve önizleme okuyabilir; ürün, eşleştirme, aktarım ve gönderim yazamaz. Kaynak/özel ağ kısıtları mevcut entegrasyonlarla aynıdır. Mağaza kimlik bilgileri ürün formlarında alınmaz.

## Büyük veri ve gerçek sınırlar

Merkezi arama/filtreler sayfalamadan önce sunucuda uygulanır. Ekranlar 25 satır gösterir. Uzak mağazadan yalnızca istenen doğal sayfa çekilir; Amazon'un 20 kayıtlık devam bilgisi ve Ticimax kartlarının varyant alt sayfaları korunur. Aktarım önizlemeleri 10 dakika geçerlidir, en fazla 32 kaynak sayfası önbelleğe alınır; tek doğal sayfa 5.000 varyantı aşarsa kimlikle daraltma istenir. SKU/barkod aramasını desteklemeyen sağlayıcıda tüm kataloğu indirerek arama yapılmaz.

100.000 ürünle filtre ve 25 satırlık yanıt kontrolü geçti. Bu bir gerçek trafik/üretim yük testi değildir. Dosya saklaması bütün veri dosyasını yeniden yazar; birden fazla sunucu ve yüksek eşzamanlı kullanım için veritabanı, indeksler ve dağıtık iş sırası gerekir.

## Kontroller

- `dotnet run --project tests/MarketPilot.CatalogChecks`: kimlikler, kısmi aktarım geri alma, demo/gerçek ayrımı, ondalık/bilinmeyen değerler, gerçek yazma engeli, eski sürüm reddi, yeniden başlatma, 100 bin ürün ve yedek kapsamı.
- `node tests/catalog-http-checks.cjs`: sekiz ekran, mağazadan aktarım, merkezi/mağaza stok ayrımı, mağaza değişikliklerinin görünmesi, üç mağazaya demo gönderim, işlem kimliği/sürüm kontrolü, eşleştirme, kategori, sipariş/gönderi bağlantıları, merkezden kataloğa geçiş, mobil ve roller.
- Mevcut liste, oturum/yedek, demo, entegrasyon, Ticimax, ön yüz ve sağlayıcı protokol kontrolleri de çalıştırılır.

## Tamamlanmış sayılmayan işler

Gerçek hesapla uçtan uca doğrulama, gerçek ürün yayını/stok yazma, zamanlanmış tam hesap aktarımı, webhook, veritabanı, çoklu depo/rezervasyon, set bileşen stokları, komisyon ve maliyet kuralları, gerçek e-fatura/ödeme/mutabakat bu çalışmayla tamamlanmış değildir. Katalog bağlantısı bu modüllerin tamamının bittiği anlamına gelmez.
