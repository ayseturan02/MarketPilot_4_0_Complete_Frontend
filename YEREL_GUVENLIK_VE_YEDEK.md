# Veritabanı olmadan yerel güvenlik ve dayanıklılık

27 Ekim 2026. Gerçek pazaryeri hesabı bağlanmadı ve hiçbir gerçek mağaza işlemi gönderilmedi.

## Kullanım

1. Uygulamada **Kullanıcı & Roller** sayfasını aç.
2. İlk yönetici adını ve en az 12 karakterlik parolanı kendin oluştur. Varsayılan bir parola atanmadı. Bu adım tamamlanana kadar mevcut yerel demo açık kalır; sonrasında API erişimi giriş gerektirir.
3. Yönetici; personel/görüntüleyici ekleyebilir, yetki değiştirebilir ve erişimi kapatabilir. Görüntüleyici salt okunur ürün/sipariş sorgularını kullanabilir, stok/diğer kayıtları değiştiremez. Yetki değişimi ve parola değişimi eski oturumları geçersiz kılar.
4. Aynı sayfada kuyruk durumunu, son yedeği ve son sistem olaylarını kontrol et. Yönetici elle şifreli yedek oluşturabilir, indirebilir ve doğrulayabilir.

## Gerçekten çalışanlar

- Demo işleri bir saniyelik arka plan servisiyle işlenir. Tarayıcının açık olması veya listeyi yenilemek gerekmez. Sunucu durursa kayıtlı bekleyen işler sonraki başlatmada devam eder.
- Aynı işlem kimliği aynı içerikle tekrar gönderildiğinde iş çoğaltılmaz; farklı içerikle tekrar kullanılırsa reddedilir. Aynı sipariş/iade/soru için çakışan bekleyen işlemler engellenir.
- Parolalar PBKDF2-SHA256 (210.000 tur, kullanıcıya özgü salt) ile özetlenir. Oturum çerezi HttpOnly/SameSite Strict'tir; oturum süresi sekiz saattir. Giriş denemeleri IP bazında ve ayrıca hesap başına (15 dakikada 10 hatalı deneme, hangi IP’den gelirse gelsin) sınırlanır; parola özeti hesap kilidi dışında ve en fazla dört eşzamanlı işlemle hesaplanır, böylece giriş denemeleri oturumlu kullanıcıları yavaşlatamaz. Not: sınır dolan hesap 15 dakika boyunca doğru parolayla da giriş yapamaz. Mutasyonlarda kaynak ve JSON biçimi kontrol edilir.
- Demo dosyasını kaydetmek başarısız olursa bellek son kaydedilen duruma döner; başarılıymış gibi ilerlenmez.
- Sunucu açıkken 24 saatte bir AES-256-GCM ile şifreli yedek oluşturulur; son 14 yedek saklanır. Her oluşturma sonrasında şifre çözme, dosya özetleri ve JSON/XML okunabilirliği kontrol edilir.
- Yönetim ekranındaki **Doğrula** mevcut dosyaları değiştirmez. Bu, canlı uygulamaya tam geri yükleme provası değildir.
- Yalnızca `localhost`, `127.0.0.1` ve `[::1]` Host başlıkları kabul edilir (DNS rebinding koruması). Başka bir adla yayınlanacaksa `AllowedHosts` ayarına o ad eklenmelidir.
- İlk yönetici oluşturulana kadar tüm `/api` uçları yalnızca bu bilgisayardan erişilebilir.
- Her yanıtta CSP, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` ve `Referrer-Policy: no-referrer` gönderilir. CSV dışa aktarımında `= + - @` ile başlayan hücreler formül olarak çalışmaz.
- Satır içi düğme işleyicilerine giden değerler `jsArg()` ile JSON dizesi olarak kodlanır; pazaryerinden gelen kimlikler JavaScript'e dönüşemez. Trendyol yol parametrelerinde `.`/`..` ve Hepsiburada sipariş numarasında yol karakterleri reddedilir.
- Son 200 sistem olayı dosyada tutulur. Parolalar, API anahtarları, istek gövdeleri ve hata ayrıntıları olay kaydına yazılmaz. E-posta/SMS gönderimi kurulmadı.

## Saklama ve sınırlar

- Yerel hesaplar: `src/MarketPilot.Web/Data/local-accounts.json`.
- Oturum koruma anahtarları: `Data/keys`.
- Şifreli yedekler: `Data/Backups`.
- Yedek şifreleme anahtarı: `Data/backup.key`. Yedek dosyasına dahil değildir. Değerini sohbete veya kaynak koduna koyma; erişimi kısıtlı, yedekten ayrı kopyasını koru. Anahtar olmadan yedek açılamaz.
- `Data` Git dışında kalır ve yayın paketine kopyalanmaz. Dosya erişimi bu bilgisayarın kullanıcı/izin yönetimine bağlıdır; oturum koruma anahtarları bu yerel kurulumda ayrıca işletim sistemiyle şifrelenmez.
- Bu yapı tek sunucu süreci ve tek makine veri klasörü içindir; yatay ölçekleme veya yüksek erişilebilirlik sağlamaz. Aynı `Data` klasörüyle ikinci bir uygulama süreci başlatılırsa `Data/.instance.lock` kilidi nedeniyle açılmaz ve nedenini yazar. Ayrı dosyalar arasında veritabanı işlemi garantisi yoktur; yedek dosyaları bağımsız tam kopyalardır. İnternet dağıtımında yalnızca [DEPLOYMENT.md](DEPLOYMENT.md) içindeki tek sunucu Docker Compose + Caddy düzenini kullan.
- Kalıcı sipariş zaman planı, stok/fiyat gönderim kuyruğu ve mağazaya özgü kimlik profilleri eklendi; gerçek mağaza izinleri kapalıdır. İndeksli üretim veritabanı, bağımsız uzak yedek ve gerçek yük testi sonraki aşamadır. Ayrıntılar: OTOMATIK_SENKRONIZASYON.md ve MAGAZA_HESAPLARI_VE_YAZMA.md.
- Sunucu kapalıyken görev çalışmaz. Yerel HTTP başlatmasını doğrudan internete açma; dış erişimi yalnızca belgelenmiş Caddy HTTPS proxy'si ve üretim modu üzerinden ver.

## Kontroller

- `dotnet run --project tests/MarketPilot.LocalReliabilityChecks`: geçici dosyalarda hesap/parola/yetki, tekrar koruması, yeniden başlatılan iş ve şifreli yedek/tahrifat testleri.
- `node tests/local-access-http-checks.cjs`: derlenmiş uygulamayı geçici içerik kökünde ve boş yerel portta başlatır; gerçek HTTP oturum/yetki ve arka plan işlerini sınar. Ana verileri ve hesapları değiştirmez.
- Chrome'da ayrıca giriş/çıkış, kullanıcı listesi, yedek doğrulama ve mobil yönetim ekranı test edildi.
