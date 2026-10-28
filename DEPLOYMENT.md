# İnternete açık dağıtım (Docker Compose + HTTPS)

Bu kurulum tek sunucu ve tek uygulama süreci içindir. Caddy, alan adı için otomatik HTTPS sertifikası alır ve HTTP trafiğini HTTPS'e yönlendirir. Uygulama yalnızca güvenilir proxy'den gelen yönlendirme başlıklarını kabul eder; oturum çerezleri HTTPS zorunludur. Yerel başlatma davranışı değişmez.

## Gereksinimler

- Güncel Docker Engine ve Docker Compose eklentisi
- Sabit, herkese açık sunucu IP'si ve bu IP'ye yönlenen bir alan adı
- Sunucunun güvenlik duvarında TCP 80/443 (isteğe bağlı HTTP/3 için UDP 443) erişimi
- Docker'ın kullandığı `172.30.0.0/24` ile çakışmayan ağ aralığı

**Üretim sürümü uyarısı:** Uygulama ve Docker imajları şu anda .NET 8 hedefliyor. .NET 8 desteği 10 Kasım 2026'da sona eriyor; bu tarihten sonra güvenlik güncellemesi almayacaktır. Uzun süreli internet yayınına geçmeden önce uygulama hedefini, test/CI SDK'sını ve iki Docker imajını desteklenen bir LTS sürümüne yükseltip tüm kontrolleri yeniden çalıştırın.

## Kurulum ve başlatma

1. Depoyu sunucuya alın ve `.env.example` dosyasını `.env` olarak kopyalayın. `MARKETPILOT_DOMAIN` değerini kendi alan adınızla değiştirin. `.env` Git tarafından yok sayılır; içine mağaza parolası veya API anahtarı koymayın.
2. Alan adının A kaydını sunucunun IP'sine yönlendirin. IPv6 kullanmıyorsanız yanlış bir AAAA kaydı bırakmayın.
3. Gerekirse `.env` içindeki Docker alt ağını, Caddy IP'sini ve uygulama IP'sini birlikte, çakışmayan adreslerle değiştirin.
4. Sunucuda `docker compose up -d --build` çalıştırın. `docker compose logs -f caddy marketpilot` ile sertifika ve uygulama başlangıç kayıtlarını izleyin.
5. İlk yönetici hesabını sunucunun SSH tüneli üzerinden oluşturun. Yerel bilgisayarda `ssh -L 5080:127.0.0.1:5080 SUNUCU_KULLANICISI@SUNUCU` çalıştırın, `http://localhost:5080` adresini açın ve güçlü, benzersiz bir yönetici parolası belirleyin. Kurulum tamamlandıktan sonra paneli yalnızca `https://ALAN_ADINIZ` üzerinden kullanın. Uygulama portu sunucuda yalnızca loopback arayüzüne bağlıdır; internete doğrudan açılmaz.
6. `/api/health` yanıtının `200`, `status: "ok"` ve `mode: "public"` olduğunu, tarayıcı geliştirici araçlarında panelin HTTPS kullandığını ve oturum çerezinin `Secure`, `HttpOnly`, `SameSite=Strict` niteliklerini taşıdığını doğrulayın.

İlk yönetici oluşturulmadan önce internetten API kurulumu ve yönetici hesabı oluşturma engellenir. SSH tüneliyle yapılan ilk kurulumdan sonra giriş ve yetkili API kullanımı HTTPS üzerinden mümkündür. Her API isteği oturum doğrulamasından geçer; görüntüleyici rolünün yazma sınırı korunur.

## İşletim ve sınırlar

- Uygulama tek süreçtir ve JSON dosyalarını kullanır. Aynı veri hacmiyle ikinci uygulama kopyası veya yatay ölçekleme çalıştırmayın. Yüksek erişilebilirlik/çoklu kullanıcı yükü için veritabanı ve harici yedekleme mimarisi gerekir.
- Kalıcı uygulama verisi `marketpilot_data` Docker hacmindedir. Yedekleme için uygulamanın şifreli yedek özelliğini kullanın ve veri hacmiyle `Data/backup.key` dosyasını ayrı, erişimi kısıtlı bir yerde koruyun. Hacim kaybı tüm uygulama verisini siler.
- `Data/keys` içindeki ASP.NET veri koruma anahtarları oturum çerezlerini korur; veri hacmi yedekliliği olmadan kaybolmaları oturumları geçersiz kılar.
- API anahtarlarını depoya, `.env` dosyasına veya açık metin Compose ayarlarına koymayın. Gerçek mağaza kimlik bilgileri için sunucunun korumalı sır/ortam değişkeni mekanizmasını kullanın; üretim yazma izinleri varsayılan olarak kapalı kalmalıdır.
- HTTPS sertifikası alınamazsa önce DNS A/AAAA kayıtlarını, genel TCP 80/443 erişimini ve Caddy kayıtlarını kontrol edin. Sertifika alınmadan uygulama trafiğini internete açmayın.
- Yükseltme öncesi yedeği doğrulayın. Çalışan sistemi durdurmak için `docker compose down`; veriyi de silen `docker compose down -v` komutunu yedek ve açık onay olmadan kullanmayın.

## Yerel doğrulama

Dağıtım erişim davranışı `node tests/deployment-http-checks.cjs` ile geçici bir veri klasöründe sınanır. Compose yapılandırmasını doğrulamak için alan adı ayarlandıktan sonra `docker compose config --quiet` çalıştırın.
