# Bütün test sonuçları

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

## Sayım yöntemi

Ana tur: **63 komut = 58 PASS / 5 FAIL / 0 atlandı**. Bu toplam 61 mevcut test paketi (56 PASS / 5 FAIL), 1 web build ve 1 dış bağlantı engeli probe içerir. Mevcut test paketleri: 23 .NET Checks +36 betik+tenant-isolation-audit+WorkerIsolationAudit. Tek bir console paketi çok sayıda assert içeriyor; bunları ölçülmüş tekil test sayısı gibi toplamıyoruz.

Yeni kontroller ayrı ölçek: **29 tanımlı doğruluk kontrolü =20 PASS/9 FAIL**: 21 API (20/1), fulfillment invariant (0/1), yedek kapsamı (0/1), üç dashboard tıklaması (0/3), üç modal semantik/odak/Escape kontrolü (0/3). Bunlar 63 paketle toplanıp yanıltıcı “92 test” oranı üretilmedi.

Ek ölçümler:126 ekran/viewport gözlemi,30 JS syntax,3 veri boyutu ×30 arama,124 yük isteği ve159 kısa-soak isteği. Bunlar ayrı ölçüm birimleri. Hata incelemesi için accounts ve shared tanı tekrarları ana sayımı değiştirmedi; ilk başarısız sonuçlar korundu.

UI/perf ilk keşif betiğinde kilitli Nebim işlemlerine 501 ve2.1MB alan validasyonuna400 gelmesi hatalı beklentiyle FAIL işaretlenmişti. Bunlar uygulama kusuru sayılmadı: capabilities sözleşmesi501 ve alan doğrulaması400 doğru. Güncel targeted/negative kontrollere beklenen statüler açık yazıldı; global boyut testi9MiB Expect:100-continue ile413 doğruluyor. İlk keşif kanıtı ui-performance-results.json içinde aynen durur. Büyük gövdeyi bütünüyle yükleyen bir tanı tekrarında ECONNRESET oluştu (client-upload-reset.json); bu koşu başarılı gösterilmedi ve istemci mevcut güvenlik testindeki header-first yöntemine uyarlandı.

## Ana tur

| Çalıştırma | Sonuç | Saniye | Ham çıktı |
| --- | --- | --- | --- |
| offline-guard | PASS | 0.08 | [log](evidence/offline-guard.log) |
| build | PASS | 0.81 | [log](evidence/build.log) |
| MarketPilot.AccountWriteChecks | PASS | 2.07 | [log](evidence/MarketPilot.AccountWriteChecks.log) |
| MarketPilot.AmazonChecks | PASS | 1.57 | [log](evidence/MarketPilot.AmazonChecks.log) |
| MarketPilot.AutomationChecks | PASS | 2.23 | [log](evidence/MarketPilot.AutomationChecks.log) |
| MarketPilot.BoynerChecks | PASS | 1.71 | [log](evidence/MarketPilot.BoynerChecks.log) |
| MarketPilot.CanonicalChecks | PASS | 2.14 | [log](evidence/MarketPilot.CanonicalChecks.log) |
| MarketPilot.CatalogChecks | PASS | 4.98 | [log](evidence/MarketPilot.CatalogChecks.log) |
| MarketPilot.ChannelContractChecks | PASS | 1.45 | [log](evidence/MarketPilot.ChannelContractChecks.log) |
| MarketPilot.DemoChecks | PASS | 5.72 | [log](evidence/MarketPilot.DemoChecks.log) |
| MarketPilot.ErpReadModelChecks | PASS | 1.61 | [log](evidence/MarketPilot.ErpReadModelChecks.log) |
| MarketPilot.ErpTransferChecks | PASS | 1.51 | [log](evidence/MarketPilot.ErpTransferChecks.log) |
| MarketPilot.HepsiburadaChecks | PASS | 1.38 | [log](evidence/MarketPilot.HepsiburadaChecks.log) |
| MarketPilot.IntegrationChecks | PASS | 1.47 | [log](evidence/MarketPilot.IntegrationChecks.log) |
| MarketPilot.ListingChecks | PASS | 1.60 | [log](evidence/MarketPilot.ListingChecks.log) |
| MarketPilot.LocalReliabilityChecks | PASS | 5.24 | [log](evidence/MarketPilot.LocalReliabilityChecks.log) |
| MarketPilot.N11Checks | PASS | 1.35 | [log](evidence/MarketPilot.N11Checks.log) |
| MarketPilot.NebimChecks | PASS | 1.48 | [log](evidence/MarketPilot.NebimChecks.log) |
| MarketPilot.OrchestratorChecks | PASS | 1.40 | [log](evidence/MarketPilot.OrchestratorChecks.log) |
| MarketPilot.PricingChecks | PASS | 2.07 | [log](evidence/MarketPilot.PricingChecks.log) |
| MarketPilot.SecurityChecks | PASS | 1.46 | [log](evidence/MarketPilot.SecurityChecks.log) |
| MarketPilot.SynchronizationChecks | PASS | 1.79 | [log](evidence/MarketPilot.SynchronizationChecks.log) |
| MarketPilot.TicimaxChecks | PASS | 1.55 | [log](evidence/MarketPilot.TicimaxChecks.log) |
| MarketPilot.WmsChecks | PASS | 2.88 | [log](evidence/MarketPilot.WmsChecks.log) |
| MarketPilot.WmsLogisticsChecks | PASS | 3.36 | [log](evidence/MarketPilot.WmsLogisticsChecks.log) |
| accounts-http-checks.cjs | FAIL | 7.54 | [log](evidence/accounts-http-checks.cjs.log) |
| amazon-http-checks.cjs | PASS | 13.25 | [log](evidence/amazon-http-checks.cjs.log) |
| amazon-protocol-checks.cjs | PASS | 0.14 | [log](evidence/amazon-protocol-checks.cjs.log) |
| analytics-system-http-checks.cjs | PASS | 5.73 | [log](evidence/analytics-system-http-checks.cjs.log) |
| automation-http-checks.cjs | PASS | 40.03 | [log](evidence/automation-http-checks.cjs.log) |
| boyner-http-checks.cjs | PASS | 14.79 | [log](evidence/boyner-http-checks.cjs.log) |
| canonical-http-checks.cjs | PASS | 2.08 | [log](evidence/canonical-http-checks.cjs.log) |
| catalog-http-checks.cjs | FAIL | 41.41 | [log](evidence/catalog-http-checks.cjs.log) |
| catalog-stock-actions-http-checks.cjs | PASS | 11.28 | [log](evidence/catalog-stock-actions-http-checks.cjs.log) |
| central-stock-http-checks.cjs | PASS | 28.48 | [log](evidence/central-stock-http-checks.cjs.log) |
| clothing-catalog-http-checks.cjs | FAIL | 32.46 | [log](evidence/clothing-catalog-http-checks.cjs.log) |
| deployment-http-checks.cjs | PASS | 1.14 | [log](evidence/deployment-http-checks.cjs.log) |
| enterprise-sync-http-checks.cjs | PASS | 73.72 | [log](evidence/enterprise-sync-http-checks.cjs.log) |
| erp-read-model-http-checks.cjs | PASS | 3.26 | [log](evidence/erp-read-model-http-checks.cjs.log) |
| erp-webhook-resilience-http-checks.cjs | PASS | 13.89 | [log](evidence/erp-webhook-resilience-http-checks.cjs.log) |
| frontend-checks.cjs | PASS | 0.09 | [log](evidence/frontend-checks.cjs.log) |
| fulfillment-http-checks.cjs | PASS | 2.33 | [log](evidence/fulfillment-http-checks.cjs.log) |
| hepsiburada-http-checks.cjs | PASS | 16.66 | [log](evidence/hepsiburada-http-checks.cjs.log) |
| integration-orchestrator-http-checks.cjs | PASS | 190.01 | [log](evidence/integration-orchestrator-http-checks.cjs.log) |
| listing-protocol-checks.cjs | PASS | 0.07 | [log](evidence/listing-protocol-checks.cjs.log) |
| local-access-http-checks.cjs | PASS | 9.58 | [log](evidence/local-access-http-checks.cjs.log) |
| n11-http-checks.cjs | PASS | 14.61 | [log](evidence/n11-http-checks.cjs.log) |
| nebim-http-checks.cjs | PASS | 4.02 | [log](evidence/nebim-http-checks.cjs.log) |
| operational-preparation-http-checks.cjs | FAIL | 1.26 | [log](evidence/operational-preparation-http-checks.cjs.log) |
| pricing-http-checks.cjs | PASS | 3.44 | [log](evidence/pricing-http-checks.cjs.log) |
| product-store-stocks-checks.cjs | PASS | 0.05 | [log](evidence/product-store-stocks-checks.cjs.log) |
| retired-channel-http-checks.cjs | PASS | 7.62 | [log](evidence/retired-channel-http-checks.cjs.log) |
| security-hardening-http-checks.cjs | PASS | 2.31 | [log](evidence/security-hardening-http-checks.cjs.log) |
| shared-workspace-http-checks.cjs | FAIL | 40.21 | [log](evidence/shared-workspace-http-checks.cjs.log) |
| synchronization-http-checks.cjs | PASS | 28.54 | [log](evidence/synchronization-http-checks.cjs.log) |
| tenant-security-http-checks.cjs | PASS | 1.66 | [log](evidence/tenant-security-http-checks.cjs.log) |
| ticimax-http-checks.cjs | PASS | 12.24 | [log](evidence/ticimax-http-checks.cjs.log) |
| trendyol-workflows-checks.cjs | PASS | 0.07 | [log](evidence/trendyol-workflows-checks.cjs.log) |
| webhook-http-checks.cjs | PASS | 17.55 | [log](evidence/webhook-http-checks.cjs.log) |
| wms-inventory-http-checks.cjs | PASS | 2.36 | [log](evidence/wms-inventory-http-checks.cjs.log) |
| wms-logistics-http-checks.cjs | PASS | 2.52 | [log](evidence/wms-logistics-http-checks.cjs.log) |
| tenant-isolation-audit | PASS | 6.79 | [log](evidence/tenant-isolation-audit.log) |
| worker-isolation-audit | PASS | 2.57 | [log](evidence/worker-isolation-audit.log) |

## Test türü kapsamı

| Tür | Durum | Gerçek kapsam / sınır |
| --- | --- | --- |
| Unit / iş kuralı / regresyon | Çalıştırıldı | 23 console Checks projesi PASS; bunların bir kısmı integration/contract kontrolleri de içeriyor. Framework bazında ayrı case/branch sayacı yok. |
| HTTP integration / API / contract | Çalıştırıldı | 36 mevcut betiğin tamamı denendi; altı kanal native fixture ve sahte HTTP testleri. 5 betik FAIL. |
| E2E / smoke / sanity / functional | Çalıştırıldı | Mock katalog, fiyat, sipariş→ERP/WMS, outbox/webhook, mapping/Ignore UI; canlı değil. |
| Negative / boundary / data integrity | Çalıştırıldı | Yeni 21 API kontrolü 20 PASS/1 FAIL; null, {}, malformed, 9MiB header, origin ve health. Mevcut unit sınır testleri de var. |
| Concurrency / race | Çalıştırıldı | CanonicalStore atomik rezervasyon PASS; yeni fulfillment çapraz sipariş kapasite FAIL (30→60). |
| Idempotency / sırasız / source-version | Çalıştırıldı | Canonical, synchronization, ERP transfer, stock outbox, WMS logistics ve orchestrator paketleri. |
| UI / Playwright / responsive | Çalıştırıldı | 42 güncel menü ekranı × 1440/768/390 =126 açılış; 0 sayfa taşması, taramada 0 pageerror. Gerçek dashboard tıklamasında 3 hata ayrıca doğrulandı. |
| Accessibility | Kısmi çalıştırıldı | DOM semantiği, isim/etiket heuristiği ve gerçek parola modalı odak/Escape. Tam WCAG/axe/ekran okuyucu testi yok. |
| Browser compatibility | Kısmi | Yalnız Chrome 156. Firefox ve WebKit binary yok; Edge/Safari cihazı testi yok. |
| Authentication / authorization / tenant | Çalıştırıldı | Yanlış/doğru parola, oturum yenilenmesi/iptali, yatay/dikey rol ihlali, header/query/body tenant ve dosya/export/log sınırları. |
| Security / nonfunctional | Çalıştırıldı / sınırlı | SSRF/DNS/URL, CSRF/CORS/CSP/header, webhook imza/replay, secret/PII masking, CSV injection. Tam penetrasyon testi değil. |
| Queue / worker / retry / recovery | Çalıştırıldı | 23 ilgili birim kontrolü içinde ve HTTP paketleri; ayrıca tenant-isolation ve worker-isolation audit PASS. |
| Backup / restore | Çalıştırıldı / FAIL | Şifre/hash/tamper pozitifleri geçti. Yeni probe Create/Verify/RestoreCheck ok iken 6 modülün eksikliğini kanıtlıyor. Eski operasyon paketinin restore satırı erken FAIL nedeniyle yürütülmedi. |
| Performance / load / controlled stress | Çalıştırıldı / sınırlı | 100/1k/10k sentetik arama; concurrency 1/4/8, toplam124 HTTP; tavan512MiB. Kuyruk tavan kapasitesi ölçülmedi. |
| Controlled soak | Kısa smoke | 30sn159 HTTP/0 hata. Saatler/günler boyu dayanıklılık veya bellek sızıntısı kanıtı değil. |
| Dependency vulnerability | Sınırlı | npm audit --offline cache sonucu0; çevrim içi advisory güncelliği yok. NuGet PackageReference0; framework/container CVE taraması yok. |
| CI/CD | Kısmi | Yerel build/suite ve Compose config; GitHub runner, Linux Chromium, Docker build/deploy/TLS yürütülmedi. |
| Static analysis | Çalıştırıldı / sınırlı | 173 dosya envanteri,30 JS syntax PASS, .NET build0 warning/error. SAST/taint/Sonar ve satır/branch coverage yok. |

## Yeniden çalıştırma

Offline restore için tests/qa/empty-feed klasörünü oluştur, OfflineHook, GuardProbe, Probes projelerini bu kaynakla NuGetAudit=false restore/build et. Mevcut web/test obj/assets hazır olmalı; hazırlık yoksa internete otomatik çıkma. Sonra:

```powershell
node tests/qa/run-audit.cjs
node tests/qa/run-probes.cjs
node tests/qa/run-extended.cjs --targeted
python tests/qa/static-audit.py
python tests/qa/inventory.py
```

Probes performans çıktısını hazırladıktan sonra tam extended koşusu UI/yük/soak yapar. `--targeted` bu yükü tekrar etmeden 21 API ve6 düğme/modal kontrolü çalıştırır. run-probes ve targeted kusurları gördüğünde exit1 verir. Ana runner ilk başarısız pakette diğerlerini atlamaz. Ham makine sonuçları: [evidence/test-results.json](evidence/test-results.json), [evidence/targeted-results.json](evidence/targeted-results.json), [evidence/ui-performance-results.json](evidence/ui-performance-results.json), [evidence/invariants-probe.json](evidence/invariants-probe.json), [evidence/backup-probe.json](evidence/backup-probe.json).

## Çalıştırılmayanlar

Canlı altı kanal/Nebim/WMS/taşıyıcı/ödeme, gerçek DB/transaction/migration/failover; Firefox/WebKit/Edge/Safari; uzun soak, çok süreçli ölçek, kuyruk saturasyonu; güncel dış advisory/SAST/DAST; Docker image ve gerçek TLS dağıtımı. Bunlar mevcut paketlerde skip edilmiş test sayısı olarak **0** toplamına gizlenmedi; ayrı **Doğrulanmadı** kapsamıdır. Otomatik ölçülmüş satır/dal kapsamı veya ürün tamamlanma yüzdesi yok.
