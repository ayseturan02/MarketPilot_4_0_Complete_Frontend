# ERP / WMS / pazaryeri güvenlik sınırları

Bu çalışma gerçek API veya veritabanı açmaz. ERP/WMS native adaptörleri mock olarak kalır. Üretim (`Deployment:Mode=public`) gerçek bağlantıları ayrıca `IntegrationSecurity:AllowRealConnections=true` verilmedikçe reddeder. Bu izin tek başına yazma izni vermez; mevcut bağlantı, tenant, marketplace write ve fiyat onay korumaları da uygulanır.

## Kimlik bilgileri

`CredentialProfiles` tenant'a özel sunucu yapılandırmasını kullanır; bağlantı kayıtlarına anahtar kopyalamaz. Profil metadata'sı API anahtarı, parola veya OAuth token içermez. `CredentialReference` ERP/WMS için tenant, sistem, sağlayıcı ve sunucu anahtar referansıdır. JSON demo deposunda yalnız referans saklanır; sır değeri environment veya key-per-file üzerinden çözülür. Referansın tenant ve ERP/WMS tipi bağlantıyla uyuşmalıdır. Gerçek bağlantı varsayılan kapalıdır.

Startup, yüklenen JSON yapılandırma sağlayıcısında dolu anahtar/parola/token alanı bulursa değerini göstermeden durur. JSON'a anahtar koyup environment ile üzerine yazmak da kabul edilmez. Örnek dosyalar gerçek anahtar içermez. Yerel kullanıcı dosyasında mevcut salt + PBKDF2 hash saklanmaya devam eder; düz parola saklanmaz.

## Oturum / API / tarayıcı

- Mevcut HttpOnly, SameSite=Strict cookie, oturum version iptali ve rol/tenant üyelik kontrolü korunur. Public cookie HTTPS zorunludur.
- JSON mutasyonlarında Origin ve Sec-Fetch-Site kontrolü vardır. Cross-site istek reddedilir; CORS üzerinden başka origin'e API yetkisi verilmez. Cookie'siz webhook ayrı imza/API-key sınırındadır.
- Tenant kontrolü header, query ve iç içe JSON kaynak/hedef alanlarında backend'de yapılır. URL harf büyüklüğü rol kontrolünü atlatmaz.
- nosniff, DENY, no-referrer, COOP, CORP, Permissions-Policy ve public HTTPS HSTS uygulanır. API/hook yanıtı no-store'dur. Hata yanıtı başlıkları temizlense bile yanıt başlamadan güvenlik başlıkları geri eklenir.
- CSP dış script kaynağını self ile sınırlar, bağlantıları self ile sınırlar, object/base/frame kullanımını engeller. `script-src-elem 'self'` modern tarayıcıda inline script bloklarını engeller. **Mevcut dinamik arayüz onclick vb. olay öznitelikleri ve inline stiller kullanır; bunların unsafe-inline uyumluluk izni devam eder. Tam katı CSP için olayların addEventListener'a ve stillerin sınıflara taşınması ayrı frontend işi olarak kalır.** CSP tek başına XSS garantisi değildir; mevcut escaping korunur.

## Dış servisler / SSRF

`OutboundTargets`, özel ERP köprüsü ve harici stok kaynağında uygulanır. Onaylı BaseUrl ile scheme/host/port aynı olmalıdır; kullanıcı bilgisi, yanlış scheme ve yönlendirme kabul edilmez. Public dağıtımda HTTP reddedilir. DNS sonuçlarının tamamı kontrol edilir ve socket doğrudan doğrulanmış IP'ye bağlanır; otomatik proxy kapalıdır. DNS/profile değişiminde eski bağlantı havuzu üzerinden kural atlanmaması için köprü bağlantıları her istekte yeniden doğrulanır.

Özel/loopback/link-local/mapped adresler varsayılan reddedilir. Gerçek bir iç ağ ERP/WMS köprüsü için bağlantının **tam IP adresleri** `AllowedPrivateAddresses` altında sunucu tarafından onaylanabilir; subnet veya kullanıcı payload'ı bu izni vermez. Metadata, unspecified ve multicast hedefleri bu izinle açılmaz. Loopback sahte sunucu testleri de tam fixture adresini açıkça onaylar; genel SSRF koruması kapatılmaz.

```json
{
  "Enterprise": {
    "Connectors": {
      "erp": {
        "BaseUrl": "https://erp-bridge.example.invalid",
        "AllowedPrivateAddresses": []
      }
    }
  }
}
```

Bu URL örnek hedef referansıdır; Nebim endpoint sözleşmesi değildir. ApiKey bu JSON'a eklenmez. Harici stok kaynağında aynı politika `Marketplaces:StockSource:AllowedPrivateAddresses` altındadır. Ticimax'ın mevcut DNS/socket, HTTPS, XML/DTD ve özel hedef korumaları korunur; diğer pazaryerlerinin sabit native host ve redirect korumaları değiştirilmez.

## Webhook / kaynak sınırları / dosyalar

ERP kaynak webhook'u ham gövdeyi timestamp + HMAC-SHA256 ile doğrular, beş dakikalık pencereyi ve sabit zamanlı karşılaştırmayı kullanır. Pazaryeri callback'inin mevcut API-key/Basic, anahtar rotasyonu, satıcı eşlemesi, TLS, kota, gövde boyutu ve kalıcı inbox korumaları devam eder. Tekrar teslim aynı kaydı çoğaltmaz.

Uygulama genelinde gövde 8 MiB, toplam header 32 KiB, header sayısı 64, header bekleme süresi 15 saniye, bağlantı sayısı 256 ile sınırlandırılır. Daha dar endpoint limitleri (örneğin sipariş webhook'u 2 MiB) devam eder. Köprüde bağlantı süresi, header boyutu, bağlantı sayısı, yanıt boyutu ve sayfa/kayıt limitleri bulunur. Limit/binding hataları ayrıntı veya sır göstermeyen yanıt üretir.

Static dosya kökü wwwroot'tur; Data, anahtar dosyaları, .git veya yapılandırma dosyaları buradan sunulmaz. Yedeklerin mevcut yetki, şifreleme, path traversal, bütünlük ve dosya adı kontrolleri korunur.

## Loglar

LocalAudit yeni mesajlarda yapılandırılmış sırları, credential biçimlerini, e-posta ve telefon biçimlerini maskeler; newline enjeksiyonunu ve mesaj boyutunu sınırlar. Eski audit mesajları okunurken de maskeleme uygulanır. İş verileri yerine hata türü/genel mesaj loglanır. HttpClient URL logları ve normal request query logları bastırılır. Console ve uygulamanın audit deposu kullanılır; Windows Event Log erişim izni giriş işlemini bozamaz. Bu maskeleme, her olası serbest metin kişisel verisini tanıyabileceği iddiasında bulunmaz; ham müşteri gövdelerini loglamamak esas kuraldır.

## Doğrulama

`npm.cmd run check:security`: mock sırlar, SSRF hedefleri/mapped IP/metadata, exact-IP izin kapsamı, redirect reddi, public bağlantı/TLS varsayılanları, JSON sır reddi, maskelenmiş audit, cookie/CSRF/CORS, CSP tarayıcı testi, hata başlıkları, boyut limiti, özel dosyalar, tenant/yetki, imzalı ERP webhook'u, mevcut pazaryeri callback ve public dağıtım kontrolleri.

Ek regresyonlar: AccountWriteChecks, SynchronizationChecks, TicimaxChecks, LocalReliabilityChecks ve sahte ERP köprüsü HTTP testleri. Canlı API çağrısı yapılmaz; fixture transport veya onaylı loopback hedefleri kullanılır.
