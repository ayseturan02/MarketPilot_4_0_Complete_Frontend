# Frontend / UI raporu

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

**42 menü ekranı ×3 genişlik =126 açılış.**1440×1000,768×1024,390×844. Açılış taramasında0 uncaught pageerror,0 API500 ve0 document yatay taşma. Bu sonuç bütün düğmelerin çalıştığı anlamına gelmiyor: gerçek tıklamada3 dashboard düğmesi FAIL.30 JS dosyası parser kontrolü PASS olmasına rağmen HTML içinde oluşan inline handler bozuk (MP-QA-004).

Chrome156 kullanıldı. Firefox/WebKit binary yok; Edge/Safari ve gerçek mobil cihaz testi yapılmadı. Dış resim/font istekleri test katmanında engellendi; canlı görsel erişilebilirliği doğrulanmadı. Mock ERP yerel SVG’si mevcut erp-read-model testinde yüklendi.

## Ekran/backend envanteri

| Ekran ID | Başlık | Bağlandığı katman / sınır | Gözlem |
| --- | --- | --- | --- |
| dashboard | Genel Bakış | overview/reports;3 düğme hatalı | 3 genişlik açıldı |
| connections | Satış Kanalları | IntegrationRegistry / central-stock | 3 genişlik açıldı |
| sync | Senkronizasyon | EnterpriseSync / automation / outbox | 3 genişlik açıldı |
| products | Tüm Ürünler | ERP katalog: salt okunur + preview/apply | 3 genişlik açıldı |
| mapping | Ürün Eşleştirme | Catalog mapping + aday arama | 3 genişlik açıldı |
| inventory | Stok & Depolar | WMS inventory / policy / history | 3 genişlik açıldı |
| barcodes | Barkod Merkezi | ERP salt okunur | 3 genişlik açıldı |
| pricing | Fiyat & Komisyon | ERP fiyat policy / preview / approval (mock) | 3 genişlik açıldı |
| categories | Kategori & Özellikler | Giyim kategori binding | 3 genişlik açıldı |
| listings | İlan & Yayın Yönetimi | Catalog ilan/job hazırlığı | 3 genişlik açıldı |
| bulk | Toplu İşlemler | Catalog seçili ilan hazırlığı | 3 genişlik açıldı |
| orders | Siparişler | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| wms-fulfillment | WMS Fulfillment | WMS workflow / policy (mock) | 3 genişlik açıldı |
| picking | Toplama & Paketleme | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| shipping | Gönderiler | WMS lojistik | 3 genişlik açıldı |
| labels | Kargo Etiketleri | WMS etiket yetkisi | 3 genişlik açıldı |
| tracking | Kargo Takip | WMS lojistik | 3 genişlik açıldı |
| returns | İade & İptaller | WMS iade/kalite onayı | 3 genişlik açıldı |
| refunds | Geri Ödemeler | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| invoices | Fatura Merkezi | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| invoice_drafts | Fatura Taslakları | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| efatura | e-Fatura & e-Arşiv | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| invoice_archive | Fatura Arşivi | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| invoice_matching | Fatura Eşleştirme | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| credit_notes | İade Faturaları | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| payments | Hakediş & Ödemeler | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| reconciliation | Mutabakat | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| trendyol | Trendyol Merkezi | Kanal native/demo backend; testlerde mock/fixture | 3 genişlik açıldı |
| amazon | Amazon Merkezi | Kanal native/demo backend; testlerde mock/fixture | 3 genişlik açıldı |
| hepsiburada | Hepsiburada Merkezi | Kanal native/demo backend; testlerde mock/fixture | 3 genişlik açıldı |
| n11 | n11 Merkezi | Kanal native/demo backend; testlerde mock/fixture | 3 genişlik açıldı |
| boyner | Boyner Merkezi | Kanal native/demo backend; testlerde mock/fixture | 3 genişlik açıldı |
| ticimax | Ticimax Merkezi | Kanal native/demo backend; testlerde mock/fixture | 3 genişlik açıldı |
| marketplace_rules | Kanal Kuralları | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| messages | Müşteri Soruları | Workspace/Operations API; yerel talep/taslak, native işlem değil | 3 genişlik açıldı |
| reports | Raporlar | Analytics API | 3 genişlik açıldı |
| profit | Kârlılık Analizi | Analytics maliyet/fiyat API | 3 genişlik açıldı |
| alerts | Bildirimler | Analytics/queue bulguları | 3 genişlik açıldı |
| activity | İşlem Kayıtları | Audit/işlem kayıtları | 3 genişlik açıldı |
| automation | Otomasyonlar | Zamanlayıcı/policy | 3 genişlik açıldı |
| users | Kullanıcı & Roller | LocalAccess / Tenant / yedek API | 3 genişlik açıldı |
| settings | Genel Ayarlar | Kalıcı ayarlar | 3 genişlik açıldı |

## Etkileşimler ve kullanıcıya gösterilen durum

Mevcut UI paketleri ürün oluşturma yasağı, katalog fark uygulama, kategori/form, filtre/sayfalama, altı kanal merkezi, fiyat onayı, alias katalog eşleme, Yoksay iptal/onayı, outbox, ERP hatası/timeout görünümü ve mock WMS senaryolarının önemli bölümlerini içeriyor. Başarısız5 paket içindeki hatadan sonraki adımlar çalıştırılmış sayılmadı. Görsel olarak bütün ekranın açılması her buton/mutasyon için E2E kapsama eşdeğer değil.

Yeni gerçek düğme testinde Satış Kanalları oku, Son Siparişler Tümünü gör ve Son İşlemler oku JavaScript SyntaxError verdi. Görsel durumu [evidence/targeted-results.json](evidence/targeted-results.json) ve app.js:17/21 destekliyor. Genel router menü linkleri ile page.evaluate(nav) çalışıyor; bu üç button bozuk.

Gerçek parola modalında dialog rolü/aria-modal, ilk odak ve Escape kapatma yok;3 test FAIL. Masaüstü taramasında2 görünür adsız ikon düğmesi gözlendi (aynı dashboard okları). Tablet/mobilde bütün42 ekrandaki hamburger düğmesinin erişilebilir adı ve aria-expanded bilgisi yok (MP-QA-012). İçerik formlarında test edilen görünür input/select için etiketsiz kontrol tespit edilmedi; bu bir accessible-name algoritması veya WCAG sertifikası değil. Kontrast, zoom200%, Tab döngüsü ve ekran okuyucu backlog.

ERP/WMS sayfaları mock kaynağını ve onay/sorgu/blocked durumlarını açıklıyor. nativeAdapter/provider-contract-pending backend bilgisi var; live başarı iddiası yapılmıyor. Pricing boş input ve eski kaynak görünümü mesajlarında bozuk Türkçe var (MP-QA-006, ErpPricingService.cs:77/84). WMS lojistik policy details içinde ham JSON teknik sunum kullanıyor; son kullanıcı için okunabilir politika özeti ürün iyileştirmesidir.

Eski global placeholder ve render override’ları bakım riski; catalog/shared/clothing eski seçicileri güncel WMS ekranını yanlış modelle bekliyor. Öneri: tek modül router, event-listener tabanlı düğmeler, ortak erişilebilir dialog, kararlı işlevsel test seçicileri ve rol odaklı gezinme testleri.

Kanıtlar: [evidence/ui-performance-results.json](evidence/ui-performance-results.json), [evidence/targeted-results.json](evidence/targeted-results.json), [evidence/password-modal.png](evidence/password-modal.png), [evidence/resilience/erp-duplicate-sku.png](evidence/resilience/erp-duplicate-sku.png), [evidence/resilience/erp-timeout.png](evidence/resilience/erp-timeout.png), [evidence/resilience/worker-error.png](evidence/resilience/worker-error.png).
