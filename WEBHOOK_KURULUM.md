# Sipariş webhook alıcısı — 27 Ekim 2026

## Hazır olan akış

`POST /hooks/orders/{endpointId}` → ayrı webhook kimlik doğrulaması → satıcı/paket/zaman kontrolü → kalıcı gelen bildirim sırası → `200 OK` → arka planda ortak sipariş kaydı.

Alıcıya panel çerezi gerekmez. `/api` oturum, rol, yerel erişim ve istek kaynağı kontrolleri kaldırılmadı. Tek dış bildirim yolu kendi API anahtarı veya Basic kimlik doğrulamasıyla çalışır. Varsayılan kurulumda etkin alıcı ve gerçek anahtar yoktur; gerçek abonelik oluşturulmadı.

Gelen paketler genel sipariş ve gönderi listesine girer. Paket aynı kimlikle güncellenir; yeni sipariş kopyası oluşturulmaz. Açık sipariş/gönderi/ana panel ekranı, sunucudaki sipariş sürümü değiştiğinde yaklaşık 10 saniyede yenilenir.

Alıcı siparişi kaydeder; tek başına stok düşme, iade muhasebesi veya taşıyıcı etiketi üretmez. Merkezi stok otomasyonu mevcut ayrı ayarları ve izleme kurallarıyla çalışır. Bu çalışma o otomasyonu kendiliğinden etkinleştirmez.

## Resmî sözleşme

- JSON `POST`, `content` içinde tam sipariş paketleri alınır. Paylaşılan Trendyol sipariş dönüştürücüsü kullanılır.
- API_KEY seçilirse anahtar `x-api-key` başlığında beklenir; BASIC_AUTHENTICATION da desteklenir. Alıcı anahtarını satıcı API Secret'ından ayrı üretin.
- Bildirim adresi `Trendyol`, `Dolap`, `Localhost` içermemeli. Bu yüzden alıcı yolu sağlayıcı adı taşımaz.
- Webhook'a ek olarak dönemsel sipariş sorgulama korunmalıdır. Bu alıcı tüm hesabı tarayan yeni bir dönemsel aktarım işi kurmaz. Mevcut sipariş okumaları ve merkezi stok izleyicisi kendi kapsamlarında devam eder; bütün sipariş geçmişinin eksiksiz eşitlendiği varsayılmaz.

Kaynaklar: [Webhook Model](https://developers.trendyol.com/docs/webhook-model), [Webhook yaratma](https://developers.trendyol.com/docs/webhook-yaratma).

## Canlı bağlantı sırası

1. Uygulamayı çalışır durumda tutan bir sunucu ve kalıcı veri alanı hazırlayın. Örnek dış adres: `https://bildirim.sirketiniz.com/hooks/orders/receiver01`.
2. Mevcut gerçek Trendyol mağaza bağlantısının kimliğini bulun. Demo bağlantısı kullanılamaz. Ortamı `production` veya `stage` olmalı; ilgili ortamdaki SellerId sunucuda tanımlı olmalı.
3. En az 32 karakterlik bağımsız rastgele alıcı anahtarı üretin. Aşağıdaki ayarları sunucu ortamına ekleyin. Anahtarı Git'e, URL'ye veya tarayıcıya yazmayın.
4. HTTPS ile bildirim yolunu dışarı açın. Yerel test uçlarını, paneli ve `/api` yönetim yollarını dışarı açmak gerekmez.
5. Aynı URL ve alıcı anahtarıyla Trendyol webhook aboneliği oluşturun. Mevcut `webhooks.create` işlemi gerçek hesap/yazma yetkisi ve açık onay olmadan abonelik oluşturmaz. Bu çalışma o korumayı değiştirmedi.
6. Gerçek bir bildirim geldiğinde yönetici durum kaydında işlendiğini ve ortak sipariş listesinde göründüğünü doğrulayın. Ancak bundan sonra canlı aboneliğin çalıştığı söylenebilir.

Örnek sunucu ayarları (değerler örnektir):

```text
Marketplaces__Trendyol__production__SellerId=GERCEK_SATICI_NUMARASI
Webhooks__Trendyol__Endpoints__receiver01__Enabled=true
Webhooks__Trendyol__Endpoints__receiver01__ConnectionId=GERCEK_MAGAZA_BAGLANTI_KIMLIGI
Webhooks__Trendyol__Endpoints__receiver01__AuthenticationType=API_KEY
Webhooks__Trendyol__Endpoints__receiver01__ApiKey=YENI_RASTGELE_EN_AZ_32_KARAKTER
Webhooks__Trendyol__TrustLoopbackTlsProxy=true
```

API_KEY geçişinde eski anahtar kısa süreli `...__PreviousApiKey` olarak kabul edilebilir; geçiş bitince kaldırın. Basic kullanılacaksa `AuthenticationType=BASIC_AUTHENTICATION`, `Username` ve en az 32 karakterli `Password` tanımlayın. `AllowInsecureLoopback=true` yalnızca yerel test içindir; canlı ayarı değildir.

Trendyol abonelik gövdesi şablonu:

```json
{
  "url": "https://bildirim.sirketiniz.com/hooks/orders/receiver01",
  "authenticationType": "API_KEY",
  "apiKey": "SUNUCUDAKI_AYNI_ALICI_ANAHTARI",
  "subscribedStatuses": ["CREATED", "PICKING", "INVOICED", "SHIPPED", "CANCELLED", "DELIVERED", "UNDELIVERED", "RETURNED", "UNSUPPLIED", "AWAITING", "UNPACKED", "AT_COLLECTION_POINT", "VERIFIED"]
}
```

### HTTPS proxy örneği

Uygulamayı yalnızca `127.0.0.1:5080` üzerinde çalıştırıp Caddy gibi bir HTTPS proxy ile sadece bu yolu yayımlayabilirsiniz. Aşağıdaki yapı sunucuda kurulmuş, alan adı yönlendirilmiş Caddy içindir; otomatik uygulanmadı:

```caddyfile
bildirim.sirketiniz.com {
    @orders {
        path /hooks/orders/receiver01
        method POST
    }
    handle @orders {
        reverse_proxy 127.0.0.1:5080 {
            header_up Host localhost
            header_up X-Forwarded-Proto https
        }
    }
    handle {
        respond 404
    }
}
```

Proxy yerelden bağlanır, Host'u mevcut localhost kısıtına uygun iletir. HTTPS aktarım başlığı yalnızca yerel proxy bağlantısında ve `TrustLoopbackTlsProxy=true` olduğunda kabul edilir. Dış IP'nin gönderdiği sahte `X-Forwarded-Proto` yeterli değildir. Uygulama doğrudan HTTPS sunacaksa gerçek alan adı `AllowedHosts` listesine açıkça eklenmelidir; `*` kullanmayın.

## Kalıcılık, tekrarlar ve sınırlar

- `200 OK` ancak gelen bildirim dosyaya yazıldıktan sonra döner. Dosya yazılamazsa `503` döner; başarılı alınmış gibi davranılmaz.
- Aynı mağaza, paket, kaynak zamanı ve içerik özeti tekrar gelirse aynı iş yeniden eklenmez. JSON alan sırası ve boşluk farklılığı bu özeti değiştirmez. Özet kimliktir; Trendyol'un imza/HMAC mekanizması olduğu iddia edilmez.
- `lastModifiedDate`, yoksa paket geçmişindeki en yeni `createdDate` kullanılır. İkisi de yoksa ya da zaman mantıksızsa bildirim reddedilir. Kaynak tarihi 48 saatten fazla ileride olamaz. Aynı paket için daha eski zamanlı webhook veya dönemsel okuma yeni durumu ezemez. Eşit zamanlı farklı içerikler alınış sırasıyla uygulanır.
- Sıra dosyası `Data/order-webhook-inbox.json`, sipariş kaydı `Data/demo-store.json` içindedir. İkisi de mevcut şifreli yedek kapsamındadır. Anahtar ve HTTP başlıkları sıraya yazılmaz; müşteri verisi yönetici telemetrisinde gösterilmez.
- İşleme hatası artan beklemeyle en fazla 8 kez denenir. Başarısız işler saklanır ve yönetici tekrar deneyebilir. İşlem sürerken uygulama kapanırsa iş yeniden açılışta devam eder; sipariş yazılıp sıra kaydı tamamlanmadan kapanırsa aynı paket üzerine tekrar yazılır, kopya sipariş oluşmaz.
- Tek istek en fazla 2 MiB / 100 paket; tek pakette en fazla 1.000 satır. Alıcı başına varsayılan limit dakikada 6.000 doğrulanmış istek; `Webhooks__Trendyol__RequestsPerMinute` ile ayarlanabilir.
- Sıra en fazla 10.000 kayıt / yaklaşık 32 MiB giriş sınırına sahiptir. Yedi günden eski başarılı/atlanmış kayıtlar yeni bildirim gelirken temizlenir; başarısız kayıtlar sessizce silinmez. Kapasite doluysa `503` döner. Yüksek hacimli canlı kullanım için veritabanı ve gerçek iş kuyruğu gerekir; mevcut dosya modeli birden fazla sunucuya uygun değildir.

## Yönetici kontrolü ve test

Yerel yönetici erişimiyle `GET /api/system/webhooks`: hazır alıcılar, bekleyen/işlenen/atlanmış/başarısız sayıları ve son olaylar. `POST /api/system/webhooks/{id}/retry` yalnızca başarısız işi yeniden sıraya alır. API anahtarları bu yanıtlarda bulunmaz. Hesap oluşturulmuşsa görüntüleyici ve operatör bu yönetim yolunu kullanamaz.

`node tests/webhook-http-checks.cjs` geçici veri dizininde HTTPS sınırı, API_KEY/Basic, satıcı kontrolü, gövde sınırları, kalıcı onay, tekrarlar, eski olay, sipariş/gönderi bağlantısı, yeniden başlatma, disk hatası, roller ve hız sınırını doğrular. Ayrıca tarayıcıda açık sipariş ekranına gelen bildirimin yansıması test edilir. Test gerçek Trendyol hesabına bağlanmaz veya abonelik açmaz.
