# Öncelikli backlog ve gün planı

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

Bu denetim kod düzeltmedi. Aşağıdaki süreler tek geliştirici için yaklaşık iş günleridir; teknik sözleşme/sağlayıcı cevabı için bekleme süresi hariç. Her günün çıkışı somut test kanıtı olmalı. Yerel faz boyunca gerçek bağlantı ve DB kapalı kalır.

| Sıra / gün | Öncelik | İş | Çıkış ölçütü |
| --- | --- | --- | --- |
| 1–2 | P1 | MP-QA-001 ortak tahsis otoritesi; WMS acceptance/processed/reconciliation ayrımı | 30 stok için iki30 siparişin toplam etkin tahsisi≤30; sıra/paralel/restart/timeout/iptal/partial testleri. |
| 3 | P1 | MP-QA-002 modül kayıtlı sürümlü yedek/restore | Altı eksik modül sentinel’i ayrı restore klasöründe aynı; eksik dosyada verify/restore başarı dönmez; mevcut encrypted/tamper testleri geçer. |
| 4 | P2 | MP-QA-003 backend null validation + MP-QA-004 düğme event katmanı | Eksik/null400; üç dashboard gerçek tıklaması doğru hedef ve0 pageerror; yeni ID/Origin/tenant kontrolleri kalır. |
| 5 | P2/P3 | MP-QA-007–010 beş eski paketin güncel fixture/rol/UI sözleşmesi; MP-QA-006 metin | Ana63 tur0 fail; eski güvenlik/sahiplik kuralı gevşemeden hata sonrası tüm adımlar yürür. |
| 6 | P2/P3 | MP-QA-005 ortak modal ve MP-QA-012 mobil menü erişilebilirliği | Dialog/isim/ilk odak/Tab/Escape/odak dönüşü; gerçek kullanıcı/maliyet/modal testleri; adsız ikonlar çözülür. |
| 7 | P2 | MP-QA-011 inline event dönüşümü / CSP sertleştirme | script-src-attr none ile bütün ekran ve düğmeler geçer; XSS ve role kontrolleri tekrar geçer. |
| 8–9 | P2 | Veri yolları ve provider sınırlarının sadeleştirilmesi | İki IWmsProvider açık bridge/native isimlendirme; tek stok/rezervasyon sözleşmesi; event×target capability tablosu; freshness/policy/uzlaştırma örnekleri. |
| 10 | P2 | Denetim kapısı: tam regresyon, güncel test dashboard, endpoint×rol coverage envanteri | Sıfır P0/P1, uygulama500 negatifleri0, tüm required paketlerPASS; açık P2 için kabul/plan; kanıt arşivi. |
| 11+ (ayrı faz) | P1 | DB tasarımı ve native kontrat hazırlığı | Tenant unique/fk/transaction/outbox/checkpoint/migration tasarımı; yetkili sözleşme ve anonim gerçek fixtures. Bu denetimde DB kurulmayacak. |
| Sözleşme sonrası | P1 | Sağlayıcı bazında adaptör ve kontrollü sandbox doğrulama | Önce read, sonra receipt/unknown/permission/quotas; native ERP/WMS/cargo/marketplace her işlem için kanıt. Süre sözleşme belli olmadan tahmin edilmedi. |
| Dağıtım öncesi | P1 | Güvenlik/performans/DR/CI üretim kabulü | Güncel CVE/SBOM, browser/a11y, uzun soak/queue yükü, gerçek restore, TLS/rollback ve en az bir uçtan uca sandbox mutabakatı. |

## Gerçek DB ve canlı faz kapıları

1. MP-QA-001/002 kapanmış, test edilmiş; bütün yerel required regresyonlar yeşil. Test adedi değil iş senaryoları kabul edilir.
2. Tenant kimliği her DB key/query/unique constraint/queue/audit nesnesinde zorunlu; veri göçü ve rollback planı; idempotency/checkpoint korunması.
3. Kaynak sahipliği ve işletmenin onayladığı fiyat/stock/fulfillment/customer/channel policy’leri sürümlü; onaysız/unknown eski veri yayını bloke.
4. Nebim/WMS/native kanal sözleşmeleri gerçek yetkili kaynakla doğrulanmış; “HTTP gönderildi” başarı sayılmıyor; hedefte sorgulanabilir referans ve çift kayıt koruması var.
5. İzole sandbox: retry429/5xx, timeout/unknown, duplicate/out-of-order, account/tenant isolation, partial/multiwarehouse/package/cancel/return ve parasal mutabakat kanıtı.
6. Kapsamlı restore, şifre anahtarı ayrı kurtarma, retention, immutable audit ve veri sorumlusu onayı; hassas veri/secret yaşam döngüsü.
7. Güncel güvenlik taramaları, onaylı runtime/container sürümleri, CI Linux/browser, TLS/allowedhosts/proxy/secrets ve düşük yetkili dağıtım/rollback tatbikatı.
8. Anlamlı iş hacmi/SLO, kuyruk gecikmesi/kapasitesi ve uzun soak onaylanmış; kontrollü rollout, kill switch, alarm ve sorumlu operasyon ekibi hazır.

**GO:** yerel izole mock geliştirme ve bu backlog’un ilk10 günü. **NO-GO:** gerçek veri/DB’ye bağlama, production API yazması veya “proje tamamlandı” ilanı. Gerçek DB geliştirmeye hazırlık tasarımı yapılabilir; bağlama ayrı fazdır.
