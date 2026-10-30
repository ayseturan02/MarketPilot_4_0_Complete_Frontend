# Özellik matrisi

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

**Durumlar:** Tamamlandı = belirtilen yerel kapsam doğrulandı; Kısmi = akışın yalnız bir bölümü; Demo = mock/fixture; Eksik = mevcut değil; Hatalı = tekrar üretildi; Doğrulanmadı = test edilmedi. Tamamlandı, canlı sağlayıcı sertifikasyonu anlamına gelmez.

| Özellik | Durum | Sınır / bulgu | Kanıt |
| --- | --- | --- | --- |
| Ortak veri sözleşmeleri | Tamamlandı / Demo | 17 model, sürüm/replay/manuel kuyruk; native provider sözleşmesi değil | CanonicalChecks, canonical HTTP |
| SKU/barkod/varyant kimliği | Tamamlandı / Demo | Çelişkiler incelemede, ada göre birleştirme yok | CanonicalChecks, CatalogChecks |
| Nebim ürün/özellik/görsel okuma | Demo | Mock provider ve ERP projeksiyonu; native Nebim yok | NebimChecks, ErpReadModelChecks, ERP HTTP |
| Ürün oluşturma/silme/serbest ana veri | Tamamlandı (engelleme) | Ürün API 405/409, ERP metadata salt okunur; Trendyol create route engelli | erp-read-model HTTP |
| ERP değişiklik farkı / kaybolan kayıt | Tamamlandı / Demo | Eşleme korunur, kayıp kaynak incelemede, fiziksel silme yok | ErpReadModelChecks |
| Kategori / mevcut ilan eşleştirme | Kısmi | Giyim kategori UI/API ve alias eşleme çalışıyor; gerçek kanal kategori zorunlulukları doğrulanmadı | CatalogChecks; stock-actions UI; eski UI paketleri FAIL |
| ERP yetkili fiyat listesi | Tamamlandı / Demo | Kaynak listesi/kanal/KDV/kuralsız kur çevrimi engeli | PricingChecks, pricing HTTP |
| Fiyat onayı / versiyon / kampanya | Tamamlandı / Demo | Masraf tabanı, sınır, expiry ve stale preview; MP-QA-006 mesajı | PricingChecks |
| Gerçek fiyat gönderimi | Eksik / kasıtlı kapalı | RealPriceWrites.Deny; mock yayın var | PricingChecks, AccountWriteChecks |
| WMS provider sözleşmesi | Demo | Capabilities, gecikme/hata/restart; native provider yok | WmsChecks, fulfillment HTTP |
| Çok depo stok ve ATS | Tamamlandı / Demo | Depolar, rezervasyon/hasarlı/bloke/transfer, limits, kanal yönetimli stok | wms-inventory HTTP |
| Fulfillment stok tahsis kapasitesi | Hatalı | Siparişler arası kapasite aşılabiliyor | MP-QA-001, invariants-probe |
| ERP/WMS yazma sırası | Tamamlandı / Demo | Onaylı policy; kabul/işlenme ayrımı, belirsizde sorgu | WmsChecks, orchestrator HTTP |
| Kısmi sevk / çok paket / depo bölme | Demo | Mock state machine ve negatifler; kapasite kusuru nedeniyle uçtan uca hazır değil | WmsChecks, WmsLogisticsChecks |
| Kargo / takip / etiket yetkisi | Demo | Kanal durum eşlemesi ve yetkili etiket bekleme; sahte belge üretilmiyor | wms-logistics HTTP |
| İade / kalite kontrol / restock | Demo | WMS onayı, kendi stoğunu doğrudan artırmama; gerçek refund yok | WmsLogisticsChecks |
| ERP sipariş aktarım hazırlığı | Tamamlandı / Demo | Kanal/cari/varyant/vergi/tutar, kabul referansı ve sorgu; native yazma yok | ErpTransferChecks, orchestrator HTTP |
| Merkezi olay orkestrasyonu | Kısmi / Demo | 12 olay türü; her olay×hedef yazma destekli değil, belirsiz yazma sorgulanıyor | OrchestratorChecks, EnterpriseSync.Integration |
| Sipariş çekme / webhook / replay | Kısmi | Native reader ve inbox; sahte HTTP ile doğrulandı, canlı teslimatlar yok | SynchronizationChecks, webhook HTTP |
| Altı kanal read/mapper/capability | Kısmi | Native adaptör + fixtures; kanal başına durum kapsamı farklı | ChannelContractChecks ve kanal paketleri |
| Kalıcı outbox / retry / worker | Tamamlandı (yerel) | Restart, hata izolasyonu, backoff/receipt; dağıtık atomiklik yok | WorkerIsolationAudit, synchronization HTTP |
| Tenant / oturum / altı rol | Tamamlandı (test edilen sınırlar) | Backend işletme bağlamı, yatay/dikey ihlal kontrolleri | tenant-security, tenant-isolation, LocalReliabilityChecks |
| Secret referans / hedef koruması | Kısmi | JSON secret reddi, masking, SSRF/DNS, credentials {} 500 | SecurityChecks; MP-QA-003 |
| Yedek ve geri alma | Hatalı / Kısmi | AES-GCM/hash doğrulama var; altı yeni modül eksik | MP-QA-002 |
| Rapor / maliyet / mutabakat | Kısmi | Yerel kayıt/hesaplamalar ve ERP tutar kontrolü; tüm native finans hareketleri değil | analytics-system HTTP, ErpTransferChecks |
| Fatura / e-Fatura / ödeme | Kısmi / Demo | Taslak ve referans; GİB/e-fatura sağlayıcısı, gerçek ödeme/iade servisi yok | WorkspaceHub, OperationalService |
| Frontend genel gezinme | Kısmi / Hatalı | 42 ekran ×3 genişlik açıldı; dashboard düğmeleri ve modal kusuru | extended UI, targeted-results |
| CI/CD / Docker / HTTPS | Kısmi | CI tanımı, Compose statik doğrulama, public proxy header testleri; TLS yayını ve image build çalıştırılmadı | deployment HTTP; compose direct |
| Gerçek DB / migration / HA / DR | Eksik (ertelenmiş) | Kullanıcı sınırına uygun olarak oluşturulmadı; canlı faz gereksinimi | Kaynak ve paket envanteri |

Başarılı yerel testler ürünün tamamlanma yüzdesi değildir. Gerçek API uyumluluğu, üretim kapasitesi ve veri tabanı dayanıklılığı bu matristen türetilemez.
