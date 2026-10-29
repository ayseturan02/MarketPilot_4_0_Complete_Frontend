# İşletme ayrımı ve kuyruk hata yalıtımı

Mevcut veriler ve kullanıcılar `default` işletmesinde korunur. İlk yönetici bu işletmenin kullanıcılarını ve yeni işletme hesaplarını yönetir. Yeni kullanıcı oluştururken **İşletme kodu** alanına örneğin `firma-a` girin. Aynı işletmede çalışacak hesaplara aynı kodu verin. Kod 1–64 küçük harf, rakam veya tire olabilir; sonradan yetki değiştirme işlemi işletmeyi değiştirmez.

Bir işletmenin yöneticisi yalnızca kendi işletmesine kullanıcı ekleyebilir ve kendi kullanıcılarını yönetebilir. Varsayılan işletmenin yöneticisi merkezi kullanıcı yönetimini yürütür. Kullanıcı adları uygulama genelinde benzersizdir. Her kullanıcı girişte kendi işletmesine açılır; istek başlığı veya URL sorgusuyla başka işletmeye geçilemez.

## Kayıt ve servis ayrımı

Yeni işletmelerin ürün, sipariş, mağaza, stok, gönderi, rapor, ayar, kuyruk, bildirim ve yedek kayıtları `Data/businesses/{işletme-kodu}/Data` içinde saklanır. Varsayılan işletmenin mevcut `Data` dosyaları taşınmaz. Her işletme kendi servis örneklerini ve arka plan görevlerini kullanır. Aynı mağaza adı, barkod veya işlem kimliği farklı işletmelerin stoklarını veya kuyruklarını birleştirmez.

Oturum ve kullanıcı üyeliği merkezi hesap deposunda tutulur; API verileri imzalı oturumdaki işletmeye göre seçilir. Başka işletmenin kayıt kimliği kendi kayıt alanında bulunmaz ve 404 döner. Toplu listeler, CSV çıktıları ve yedekler de aynı alanı kullanır. İşletme servisinin kaydı eksikse ortak uygulama verisine geri dönülmez.

## Bağlantı yapılandırması

Yeni işletmeler varsayılan işletmenin ERP, pazaryeri, kargo veya webhook anahtarlarını devralmaz. Sunucu yapılandırması `Businesses:{işletme-kodu}:...` altında tanımlanır. Örnek adlar:

- `Businesses:firma-a:MarketplaceAccounts:satici1:Marketplace`
- `Businesses:firma-a:MarketplaceAccounts:satici1:Credentials:SellerId`
- `Businesses:firma-a:Enterprise:Connectors:erp:BaseUrl`
- `Businesses:firma-a:Enterprise:Connectors:erp:ApiKey`
- `Businesses:firma-a:Enterprise:Erp:InboundKey`

Bu adlar ortam değişkeninde `:` yerine `__` ile yazılır. Bağlantı tanımı, anahtar ve yazma izinleri ilgili işletmeye ayrıca girilir. Canlı servisler bu çalışma kapsamında bağlanmadı.

ERP bildirimi `/hooks/businesses/firma-a/erp/catalog`, sipariş bildirimi `/hooks/businesses/firma-a/orders/{endpointId}` adresine gönderilir. Bu yolların her biri ilgili işletmenin kaynak imzası/API kimliğiyle doğrulanır. Varsayılan işletmenin mevcut `/hooks/erp/catalog` ve `/hooks/orders/{endpointId}` yolları korunur.

## Worker düzeltmesi

Demo kuyruk işleyicisi her işin öncesinde çalışma alanı durumunu saklar. Beklenmeyen hata veren işin kısmi değişiklikleri geri alınır, iş hata durumuna geçirilir ve diğer işler işlenir. Başarısız iş tekrar denemeye açıktır; sağlam işlerin ilerlemesini engellemez. Kalıcı dosya yazma hataları mevcut geri alma mekanizmasıyla ele alınmaya devam eder.

## Doğrulama

`node scripts/regression-audit.mjs business-isolation-audit worker-isolation-audit`

Kontroller iki işletmenin aynı adlı mağazalarda eşzamanlı stok işlemlerini, yabancı kayıt okuma/yazma reddini, ERP anahtar ayrımını, katalog/CSV ayrımını, kullanıcı yönetimi sınırlarını, ayrı yedekleri, tarayıcı görünümünü ve yeniden başlatmada kalıcılığı doğrular. Worker kontrolünde hatalı iş `failed`, sağlam iş `completed` olur ve worker çalışır kalır. Bu iki kontrol `npm.cmd run check` kapsamına da eklendi.
