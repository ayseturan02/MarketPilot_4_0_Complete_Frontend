# Güvenlik raporu

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

Test edilen sınırlar içinde yeni tenantlar arası veri sızıntısı, yetki yükseltme veya sömürülebilir XSS kanıtı bulunmadı. Bu bir “güvenlik açığı yoktur” sertifikası değil. **P1 yedek eksikliği erişilebilirlik/veri kurtarma riski; P2 CredentialReference null500 ve P2 inline handler CSP riski mevcut.**

| Kontrol | Durum | Kanıt / pratik sınır |
| --- | --- | --- |
| Parola / oturum | PASS (yerel) | PBKDF2-SHA256210k, random salt, sabit zaman kıyas; yanlış parola401/doğru200; version invalidate;8saat sliding olmayan cookie. MFA/SSO ve harici session store yok. |
| Cookie | PASS | HttpOnly, SameSiteStrict; public mod SecureAlways. Local loopbackHTTP Secure olmayabilir; bu üretimHTTPS ayarıyla karıştırılmadı. |
| RBAC / tenant | PASS (test edilen) | Altı rol, cookie membership kontrolü, source/target header/query/body reddi; tenant’a ayrı servis/JSON kökü. Yatay/dikey saldırılar, export/rapor/webhook/queue/audit/bağlantı sınırları mevcut testlerde geçti. |
| CSRF / Origin / CORS | PASS (test edilen) | Foreign Origin403, diğer site okuma/yazma sınırları; SameSite cookie/host kontrolü. Mevcut oturum düzeni değiştirilmedi; ayrı token modeli zorunlu kılınıp koruma kapatılmadı. |
| CSP / XSS | Kısmi | Escaping/jsArg ve modern script-src-elem self; inline attr unsafe-inline var (MP-QA-011). Mevcut saldırı fixture testleri PASS, bütün DOM sink kombinasyonları test edilmedi. |
| HTTP header / HTTPS | PASS (uygulama simülasyonu) | nosniff, DENY/frame-ancestors, referrer no-referrer, COOP/CORP, permissions ve no-store. Public TLS/proxy header testleri PASS; gerçek sertifika/Caddy TLS handshake denenmedi. |
| SSRF / URL / DNS | PASS (fixture) | OutboundTargets private/mapped/metadata/redirect denetimi, açık onaylı private host, DNS-pin; Ticimax HTTPS/allowlist. Gerçek DNS altyapısı ve OS firewall pentesti değil. |
| CredentialProfiles / CredentialReference | Kısmi | Secret değeri UI/JSON connection kaydına yazılmıyor. Env/key-per-file referansı; JSON secret config startup’ta reddediliyor. {} referansı500 (MP-QA-003); mock alanları için pozitifler geçti. |
| Webhook | PASS (mock) | İmza/timestamp/idempotency/secret rotasyonu ve tenant inbox, retry/duplicate/payload sınırı. Her native sağlayıcının protokolü canlı onaylanmadı. |
| Log / PII / CSV | PASS (fixture) | Authorization/config secret/email/telefon masking, genel sanitized500, CSV formül/locale koruması. Tüm olası kişisel veri desenlerinin anonimliği ispatlanmadı. |
| Dosya / path / backup | Kısmi | Private Data/keys/webroot dışı dosya erişimi ve path traversal red; şifreli/hash’li yedek tamper testi PASS; kapsam kaybı MP-QA-002. |
| Request/resource limits | PASS (örnekler) | 8MiB body, header64/32KiB, JSONdepth32, login throttling; header-first9MiB413, ardından health200. Genel DDoS dayanıklılığı değil. |
| Üretimde gerçek bağlantı | PASS (guard) | IntegrationSecurity.AllowRealConnections defaultfalse public mod; mock writes ayrı; gerçek fiyat kapalı. Operator ortamında sonradan açılacak ayarlar ayrı rollout gerektirir. |
| Dependency CVE | Doğrulanmadı / cache | npm audit --offline0 bildiriyor; current advisory registry sorgulanmadı. .NET NuGet package reference0; .NET8 runtime ve container/base image CVE/SBOM güncel taraması yapılmadı. |

## Dış bağlantının teknik engellenmesi

tests/qa/OfflineHook: HttpClient RequestOut.Start üzerinde non-loopback engeli. GuardProbe gerçek socket oluşturmayan fake ConnectCallback ile denendi: blocked=true, transportReached=false, sockets0. Node preload fetch/http(s)/Socket.connect engeli; Playwright context route non-loopback abort, service-worker block ve loopback dead proxy. Production test modu gerçek UserSecrets yüklemez; kimlik env alanları temizlendi, gerçek write varsayılanları kapatıldı. Native adapter fixture handler ve loopback sahte HTTP sunucuları kullanıldı.

Bu bir işletim sistemi firewall’u değildir: keyfi başka bir binary/raw-socket API için genel izolasyon iddiası yok. İncelenen uygulama HTTP yolları HttpClient/Node/browser üzerinden; Docker build/network ve canlı tarayıcı sayfaları bu yüzden yürütülmedi. Offline npm audit yalnız cache; Compose config ağ bağlantısı açmayan statik işlem. Kanıt: offline-guard.log, egress.log, OfflineHook/StartupHook.cs ve offline-preload.cjs.

## Üretim öncesi ek işler

- Kritik yedek kapsamı ve doğru DR anahtar yönetimi; tenant/WMS makbuzlarını içeren restore tatbikatı.
- Inline-handler kaldırıp CSP’yi sıkılaştır; mevcut kontrolleri devre dışı bırakmadan regresyon.
- Güncel runtime/container/npm advisory taraması ve SBOM; CI’de sabit güvenlik eşikleri.
- Gerçek sağlayıcı credential rotasyon/iptal/TTL, onay politikaları ve tenant bazlı erişimle native adaptöre bağlama.
- Hassas veri retention/maskeleme ve değiştirilemez harici audit; gerekliyse MFA/SSO ve oturum yönetimi.
- Yetkili ortamda bağımsız penetrasyon testi; bütün endpoint/rol matrisini üretilmiş denial testleriyle tamamla.

Kanıtlar: [evidence/MarketPilot.SecurityChecks.log](evidence/MarketPilot.SecurityChecks.log), [evidence/security-hardening-http-checks.cjs.log](evidence/security-hardening-http-checks.cjs.log), [evidence/tenant-security-http-checks.cjs.log](evidence/tenant-security-http-checks.cjs.log), [evidence/tenant-isolation-audit.log](evidence/tenant-isolation-audit.log), [evidence/webhook-http-checks.cjs.log](evidence/webhook-http-checks.cjs.log), [evidence/npm-offline-audit.log](evidence/npm-offline-audit.log).
