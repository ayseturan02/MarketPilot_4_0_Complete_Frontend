# İnternete açık dağıtım (Docker Compose + HTTPS)

Bu kurulum tek sunucu ve tek uygulama süreci içindir. Caddy, alan adı için otomatik HTTPS sertifikası alır ve HTTP trafiğini HTTPS'e yönlendirir. Uygulama yalnızca güvenilir proxy'den gelen yönlendirme başlıklarını kabul eder; oturum çerezleri HTTPS zorunludur. Yerel başlatma davranışı değişmez.

## Gereksinimler

- Linux sunucuda güncel Docker Engine ve Docker Compose eklentisi
- Sabit, herkese açık sunucu IP'si ve bu IP'ye yönlenen bir alan adı
- Sunucunun güvenlik duvarında TCP 80/443 (isteğe bağlı HTTP/3 için UDP 443) erişimi
- Docker'ın kullandığı `172.30.0.0/24` ile çakışmayan ağ aralığı

**Üretim sürümü uyarısı:** Uygulama ve Docker imajları şu anda .NET 8 hedefliyor. .NET 8 desteği 10 Kasım 2026'da sona eriyor; bu tarihten sonra güvenlik güncellemesi almayacaktır. Uzun süreli internet yayınına geçmeden önce uygulama hedefini, test/CI SDK'sını ve iki Docker imajını desteklenen bir LTS sürümüne yükseltip tüm kontrolleri yeniden çalıştırın.

## Kurulum ve başlatma

1. Depoyu sunucuya alın ve `.env.example` dosyasını `.env` olarak kopyalayın. `MARKETPILOT_DOMAIN` değerini kendi alan adınızla değiştirin. `.env` Git tarafından yok sayılır; içine mağaza parolası veya API anahtarı koymayın.
2. Alan adının A kaydını sunucunun IP'sine yönlendirin. IPv6 kullanmıyorsanız yanlış bir AAAA kaydı bırakmayın.
3. Gerekirse `.env` içindeki Docker alt ağını, ağ geçidini (`MARKETPILOT_GATEWAY_IP`), Caddy IP'sini ve uygulama IP'sini birlikte, çakışmayan adreslerle değiştirin.
4. Sunucuda `docker compose up -d --build` çalıştırın. `docker compose logs -f caddy marketpilot` ile sertifika ve uygulama başlangıç kayıtlarını izleyin.
5. İlk yönetici hesabını sunucunun SSH tüneli üzerinden oluşturun. Yerel bilgisayarda `ssh -L 5081:127.0.0.1:5080 SUNUCU_KULLANICISI@SUNUCU` çalıştırın, `http://localhost:5081` adresini açın ve güçlü, benzersiz bir yönetici parolası belirleyin. Formdaki **Kurulum kodu** alanına sunucuda `docker compose exec marketpilot cat /app/Data/setup-token.txt` komutunun çıktısını girin. Kod tek kullanımlıktır, kurulumdan sonra silinir. Sunucuya erişebilen başka bir kullanıcının veya yanlış ayarlanmış bir proxy'nin ilk yöneticiyi sizden önce oluşturmasını engeller. Yerel tünel portu 5081, bilgisayarda çalışan 5080 numaralı uygulamayla çakışmaz. Kurulum tamamlandıktan sonra paneli yalnızca `https://ALAN_ADINIZ` üzerinden kullanın. Uygulama portu sunucuda yalnızca loopback arayüzüne bağlıdır; internete doğrudan açılmaz.
6. `/api/health` yanıtının `200`, `status: "ok"` ve `mode: "public"` olduğunu, tarayıcı geliştirici araçlarında panelin HTTPS kullandığını ve oturum çerezinin `Secure`, `HttpOnly`, `SameSite=Strict` niteliklerini taşıdığını doğrulayın.

İlk yönetici oluşturulmadan önce internetten API kurulumu ve yönetici hesabı oluşturma engellenir. SSH tüneliyle yapılan ilk kurulumdan sonra giriş ve yetkili API kullanımı HTTPS üzerinden mümkündür. Her API isteği oturum doğrulamasından geçer; görüntüleyici rolünün yazma sınırı korunur.

## İşletim ve sınırlar

- Uygulama tek süreçtir ve JSON dosyalarını kullanır. Aynı veri hacmiyle ikinci uygulama kopyası veya yatay ölçekleme çalıştırmayın. Yüksek erişilebilirlik/çoklu kullanıcı yükü için veritabanı ve harici yedekleme mimarisi gerekir.
- Kalıcı uygulama verisi `marketpilot_data` Docker hacmindedir. Yedekleme için uygulamanın şifreli yedek özelliğini kullanın ve veri hacmiyle `Data/backup.key` dosyasını ayrı, erişimi kısıtlı bir yerde koruyun. Hacim kaybı tüm uygulama verisini siler.
- `Data/keys` içindeki ASP.NET veri koruma anahtarları oturum çerezlerini korur; veri hacmi yedekliliği olmadan kaybolmaları oturumları geçersiz kılar.
- API anahtarlarını depoya, `.env` dosyasına veya açık metin Compose ayarlarına koymayın; üretim yazma izinleri varsayılan olarak kapalı kalmalıdır. Ortam değişkenleri `docker inspect` ile okunabildiği için gerçek mağaza kimlik bilgilerini Compose **secrets** ile verin: uygulama `/run/secrets` altındaki her dosyayı bir ayar olarak okur (dosya adı ayar adıdır, `:` yerine `__`). Örnek `docker-compose.override.yml`:

  ```yaml
  services:
    marketpilot:
      secrets: [Marketplaces__Trendyol__production__ApiKey, Marketplaces__Trendyol__production__ApiSecret]
  secrets:
    Marketplaces__Trendyol__production__ApiKey: { file: ./secrets/trendyol-api-key.txt }
    Marketplaces__Trendyol__production__ApiSecret: { file: ./secrets/trendyol-api-secret.txt }
  ```

  `secrets/` klasörü Git dışındadır; dosya izinlerini `chmod 600` ile kısıtlayın. Farklı bir klasör için `Secrets__Directory` ayarı kullanılabilir.
- Giriş denemeleri IP başına ve ayrıca hesap başına sınırlanır. Docker'ın varsayılan *userland proxy*'si IPv6 istemcileri Caddy'ye ağ geçidi IP'siyle iletir; bu durumda tüm IPv6 kullanıcıları tek IP gibi görünür ve IP sınırını paylaşır. Gerçek istemci IP'si için sunucuda `/etc/docker/daemon.json` içine `"userland-proxy": false` ekleyip Docker'ı yeniden başlatın ya da Compose ağında IPv6'yı (`enable_ipv6: true` ve bir IPv6 alt ağı) açın.
- HTTPS sertifikası alınamazsa önce DNS A/AAAA kayıtlarını, genel TCP 80/443 erişimini ve Caddy kayıtlarını kontrol edin. Sertifika alınmadan uygulama trafiğini internete açmayın.
- Yükseltme öncesi yedeği doğrulayın. Çalışan sistemi durdurmak için `docker compose down`; veriyi de silen `docker compose down -v` komutunu yedek ve açık onay olmadan kullanmayın.

## Yerel doğrulama

Dağıtım erişim davranışı `node tests/deployment-http-checks.cjs` ile geçici bir veri klasöründe sınanır. Compose yapılandırmasını doğrulamak için alan adı ayarlandıktan sonra `docker compose config --quiet` çalıştırın.

Docker ilk kurulumunda HTTP istisnası yalnızca yapılandırılmış ağ geçidi IP’sinden, `localhost`/loopback Host değeriyle ve yönlendirme başlıkları olmadan gelen isteklere tanınır. Bu, sunucunun `127.0.0.1:5080` portuna bağlı SSH tünelini kapsar; internetten veya Caddy üzerinden gelen istekler HTTPS ve oturum denetiminden geçer. `Deployment:BootstrapGatewayIp` ayarına genel istemci veya proxy IP’si vermeyin.

CI ayrıca `docker build -t marketpilot:ci .` ve `node scripts/check-docker-deployment.cjs` ile gerçek Linux konteynerini salt okunur dosya sistemi ve kalıcı veri hacmiyle başlatır; ağ geçidi üzerinden ilk kurulum, HTTPS zorunluluğu, oturum ve yeniden başlatma sonrası verinin korunmasını doğrular.
