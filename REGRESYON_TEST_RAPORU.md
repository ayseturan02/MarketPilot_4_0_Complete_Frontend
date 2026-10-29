29 Ekim 2026 sonuç: `npm.cmd run check` tek çalıştırmada 39 grubun tamamını geçirdi; derleme 0 hata, 0 uyarı. Son düzeltmeler ve kalan işlevsel kapsam: [entegrasyon incelemesi](ENTEGRASYON_INCELEMESI.md). Ham çıktı: [integration-review-check.log](artifacts/integration-review-check.log). Aşağıdaki eski sayımlar önceki kontrollerin tarihçesidir.

# Regresyon test sonuçları — 29 Ekim 2026

Güncel ek kapsam: [veritabanı bağlanmadan operasyon hazırlığı](VERITABANSIZ_OPERASYON_HAZIRLIGI.md). Rezervasyon, kısmi işlemler, paket devri, hakediş, dış aktarım ve izole geri yükleme testleri eklendi. Toplam 39 grubun sonucu ve n11 form düzeltmesi sonrası yapılan tekrar doğrulaması bu belgede kayıtlıdır.

Ek doğrulama: [Sahte ERP ve webhook dayanıklılığı](ERP_WEBHOOK_DAYANIKLILIK_TESTI.md). Tekrarlanan SKU, yanıt gövdesi zaman aşımı, webhook tekrarları ve işçi kayıt hatasının ekranda görünmesi HTTP/tarayıcı testleriyle doğrulandı.

Kullanıcının verdiği 12 senaryo, mevcut yerel uygulama akışlarıyla eşleştirildi. Testler ayrı geçici veri klasörlerinde, örnek kimlikler ve sahte servislerle çalıştırıldı. Çalışan yerel uygulamanın kayıtları değiştirilmedi. Canlı pazaryeri/Nebim/WMS/kargo bağlantısı denenmedi. İlk kontrolde başarısız olan işletme ayrımı ve worker hata yalıtımı düzeltildi; aşağıdaki tablo düzeltme sonrası durumu gösterir.

| Test | Sonuç | Gözlenen davranış / kapsam |
| --- | --- | --- |
| Kullanıcı girişi | Başarılı | Doğru parola 200, yanlış parola 401; oturumsuz veri isteği 401. Rol kısıtları ve oturum iptali geçti. |
| Pazaryeri bağlantısı | Başarılı — sahte servis | Seller ID, kimlik başlıkları, ortam ve mağaza profili eşleşmesi geçti; eksik/uyumsuz profil reddedildi. Canlı anahtar doğrulanmadı. |
| Ürün senkronizasyonu | Başarılı — örnek veri | Mağaza ürünlerini okuma, sayfalama, alan dönüştürme, mevcut ürünlere eşleme ve ERP kaynaklı kayıt testleri geçti. Yeni ana ürün kaynağı Nebim'dir; mağazadan bağımsız yeni ürün oluşturulmaz. |
| Stok senkronizasyonu | Başarılı | Satışta stok düşümü, aynı satışın tekrar düşülmemesi, güvenlik stoğu, seçilmeyen mağazanın korunması ve gönderim kuyruğu geçti. |
| Fiyat senkronizasyonu | Başarılı | Yeni fiyat doğru mağazaya aktarıldı; yalnız stok işleminde mevcut fiyat korundu. |
| Sipariş senkronizasyonu | Başarılı | Sayfalı sipariş alımı, tekrar çekmede tek kayıt, yeniden başlatmada devam ve eski bildirimin reddi geçti. |
| Sipariş durumu | Başarılı | Hazırlama/kargolama durumu, iptal/iade ve stok iadesinin tek kez uygulanması geçti. |
| API hata yönetimi | Başarılı — simülasyon | 429 sınırlı tekrar/bekleme, 500'ün temizlenmiş 502 yanıtına dönüştürülmesi, bağlantı hatasında kalıcı retry ve belirsiz aktarımda makbuz doğrulaması geçti. |
| Çoklu mağaza / iki işletme | Başarılı | Aynı adlı mağazalarda eşzamanlı stok işlemleri bağımsız tamamlandı. Katalog, CSV, kuyruk, kaynak anahtarları ve yedekler ayrıldı; yeniden başlatmada korundu. |
| Veritabanı / tekrar sipariş | Kısmen başarılı | Tekrar istekte çift sipariş ve çift stok hareketi oluşturulmaması mevcut kalıcı kayıtlarla geçti. SQL/veritabanı transaction testi yapılamaz; kalıcılık JSON dosyalarıdır. |
| Worker hata yalıtımı | Başarılı | Hatalı iş geri alınarak `failed` durumuna geçti; başka mağazadaki sağlam iş `completed` oldu. Worker açık kaldı. |
| Başka işletmenin verisine erişim | Başarılı | İşletme üyeliği imzalı oturuma bağlandı. Diğer işletmenin mağaza/ürün okuma ve yazma istekleri 404, yetkisiz kullanıcı yönetimi 403 döndü. |

## Düzeltme öncesindeki bulgular

İlk denetimde işletme ayrımı kontrolü her iki yönde de 200 döndürmüştü ve kullanıcı diğer mağazayı listede görebilmişti. Bu eski davranış giderildi. Güncel sürümde kullanıcılar işletme koduyla atanır; imzalı oturum işletmenin ayrı veri alanını seçer. Diğer işletmenin mağazası listede görünmez ve kayıtlarına erişim 404 ile reddedilir.

Worker kontrolünde ilk demo çalışma alanının ürün listesine beklenmeyen veri hatası enjekte edildi; ikinci çalışma alanına süresi gelmiş geçerli stok işi kondu. Birden fazla worker döngüsünden sonra `workerAlive=true`, `errorRecorded=true`, `healthyJobState=pending` görüldü. Daha önceki worker testi yalnızca hatanın worker'ı sonlandırmadığını doğruluyordu; diğer işin ilerlediğini kanıtlamıyordu. Bu yeni kontrol o ayrımı yakaladı. Sonuç özellikle demo kuyruk işleyicisine aittir; bütün worker türlerine genellenmez.

## Çalıştırma ve ham sonuçlar

`node scripts/regression-audit.mjs`

İlk çalışmada 14 mevcut teknik grup geçti, iki yeni gereksinim denetimi başarısız oldu. Düzeltmelerden sonra iki denetim de 0 çıkış koduyla geçti. Ham sonuçlarda `businessIsolation.meetsRequirement=true`, `userAReadsStoreB=404`, `userBReadsStoreA=404`, `healthyJobState=completed` ve `workerAlive=true` doğrulandı. Grup sayısı, tablodaki senaryo sayısından farklıdır. Düzeltmelerin kullanımı: [İşletme ayrımı](ISLETME_AYRIMI.md).

Son doğrulamada `npm.cmd run check` tek çalıştırmada başarıyla tamamlandı: 13 .NET grubu, 22 HTTP/ön yüz grubu ve işletme/worker denetimleri dahil toplam 37 grup geçti. Derleme 0 hata ve 0 uyarıyla tamamlandı. Yeni işletme kullanıcısının tarayıcıda kendi ürünlerini görmesi, yabancı mağazanın senkronizasyon listesinde bulunmaması ve kullanıcı oluşturma formundaki işletme kodu alanı da doğrulandı.

Ham sonuçlar ve her grubun günlüğü: [artifacts/regression-audit/results.json](artifacts/regression-audit/results.json). Özel kanıtlar: [işletme ayrımı](artifacts/regression-audit/business-isolation-audit.log), [worker yalıtımı](artifacts/regression-audit/worker-isolation-audit.log).

Yalnız ilgili grupları tekrar çalıştırmak için grup kimliklerini komuta ekleyin; önceki diğer sonuçlar zaman damgalarıyla korunur. Örnek: `node scripts/regression-audit.mjs business-isolation-audit worker-isolation-audit`.
