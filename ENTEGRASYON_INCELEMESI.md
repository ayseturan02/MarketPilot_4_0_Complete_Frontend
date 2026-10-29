# Entegrasyon incelemesi — 29 Ekim 2026

Veritabanı, gerçek veri ve canlı hesap bağlantıları bu incelemenin dışında tutuldu. Bunların bulunmaması bu aşamada hata sayılmadı. Canlıya geçiş yapılmadı.

## Düzeltilen hatalar

- ERP içe aktarımında aynı SKU/barkodun demo ve gerçek kaynak kayıtlarını birbirine karıştırması engellendi.
- Aktif rezervasyonlar varken rezervasyon yönetiminin kapatılması engellendi.
- Sipariş gözlemleri, sürüm kontrolü ve stok rezervasyonları satıcı hesabına bağlandı. Hesap değişiminde aynı paket numarası eski hesapla karışmaz; eski taslak yeni hesaba gönderilemez. Önceki hesaptan kalan rezervasyonlar otomatik bırakılmaz; kaynak onayı gerekir.
- Birden fazla hesaba ait aynı paket için belirsiz stok onayı reddedilir; hesap kimliği gerekir.
- Harici iptal/iade onayında miktarlar yeniden kontrol edilir; bekleyen talepler aynı miktarı tekrar kullanamaz.
- Depo iade kabulünde siparişin güncel durumu kontrol edilir; sonradan iptal edilen siparişe tekrar stok yazılmaz.
- Silinmiş siparişe ait paket planı eski olarak işaretlenir; diğer aktarımları durdurmaz.
- Finans girdisinde uç negatif tutarın taşma hatası engellendi. Para birimi normalleştirilir; para birimi bilinmeyen sipariş için belge veya ödeme iadesi hazırlanmaz. Geri ödeme dışındaki taleplere keyfî tutar girilemez.

## Mevcut kapsam

Nebim kaynaklı katalog; mağaza/SKU/barkod eşleştirme; stok-fiyat kuyruğu; görev aralıkları ve manuel tetikleme; sipariş alımı ve tekrar kayıt koruması; ERP/WMS aktarım planı; paket/kargo takip akışı; rezervasyon; kısmi iptal ve depo kabulüyle iade; paket planlama; hakediş karşılaştırması; hata/bildirim kuyruğu; işletme ve rol ayrımı; şifreli yedek ve izole geri yükleme provası bulunuyor. Her pazaryerinin gerçek işlem kapsamı aynı değil.

## Kalan işlevsel eksikler

1. **Çok depo dağıtımı:** stok sahibi seçimi mevcut; siparişi depo kapasitesi, öncelik ve bölgesine göre depolara otomatik paylaştırma yok.
2. **Kanal bazında işlem kapsamı:** bazı iade, soru, finans, belge ve kargo ekranları demo kapsamındadır. Her kanalın desteklediği işlemler tek bir yetenek tablosuyla açıklanmalı ve eksik gerçek adaptörler tamamlanmalı.
3. **Belge yaşam döngüsü:** belge ve ödeme iadesi talepleri/aktarım kuyruğu hazır; fatura iptali, iade belgesi ve muhasebe ilişkisinin bütün zinciri tamamlanmış değil.
4. **Mutabakat derinliği:** son okunan panel/kaynak verileri ve girilen hakediş karşılaştırılır. Tüm panel verisini otomatik tamamlayan tarama, bütün bilinmeyen alanların ayrı raporu ve otomatik finans dosyası alımı eksik.
5. **Sağlayıcı uyarlaması:** mevcut ERP/WMS/kargo/belge/ödeme ara servis sözleşmesi her firmanın yerel API'siyle aynı değildir. Sağlayıcı seçilince yalnızca anahtar girmek yetmeyebilir; alan/durum eşleştirmesi ve adaptör gerekir. Şu anda canlı bağlantı yapılması gerekmiyor.
6. **Geri yükleme:** yedeğin izole alanda açılıp okunması doğrulanır; mevcut çalışma alanının kontrollü geri yüklemeyle değiştirilmesi ayrı iş akışı olarak hazırlanmalı.

Yeni ürün ekleme önerilmez; ürün kaynağı Nebim V3 olarak korunur.

## Doğrulama

`npm.cmd run check` tek çalıştırmada başarıyla tamamlandı: 13 .NET kontrol grubu, 24 HTTP/ön yüz grubu ve 2 işletme/worker denetimi dahil **39 grubun tamamı geçti**. Derleme **0 hata, 0 uyarı** verdi. [Ham çıktı](artifacts/integration-review-check.log).

Genişletilen operasyon testi aktif rezervasyonla kapatma engeli, demo/gerçek kaynak ayrımı, iptal sonrası depo kabulünün reddi, uç finans tutarı, eksik siparişli paket planının diğer işleri durdurmaması ve satıcı değişiminde aynı paket numarasının bağımsız işlenmesini de doğruladı. Bütün harici servisler yerel sahte sunuculardı.

Yerel sahte servis testleri gerçek sağlayıcının kabul testinin yerini tutmaz. Testlerin geçmesi bütün olası hataların bulunmadığı anlamına gelmez.
