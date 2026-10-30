# Hata ve risk kaydı

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

**11 doğrulanmış kusur grubu:**7 uygulama ve4 test altyapısı. Ayrıca1 CSP güvenlik riski kaydı. **P0=0, P1=2, P2=8 (risk dahil), P3=2.** Tek kök neden birden fazla test/düğme hatasını kapsayabilir. “Risk” kaydı sömürülebilir saldırı kanıtı gibi sunulmadı.

Öncelik: P0 kritik kayıp/ihlâl; P1 stok/veri kurtarma gibi yüksek iş etkisi; P2 işlev, güvenlik katmanı veya regresyon güvenilirliği; P3 kullanıcı mesajı. Native sözleşme/gerçek DB gibi ertelenmiş işler hata sayımına eklenmedi; [MISSING_FEATURES](MISSING_FEATURES.md) ve [BACKLOG](PRIORITIZED_BACKLOG.md) içinde.

| ID | Öncelik | Tür | Bulgu |
| --- | --- | --- | --- |
| MP-QA-001 | P1 | Uygulama | Siparişler arası stok tahsisi kapasiteyi aşıyor |
| MP-QA-002 | P1 | Uygulama | Yedek başarılı görünürken yeni modüller kapsam dışı |
| MP-QA-003 | P2 | Uygulama | Eksik CredentialReference.System HTTP 500 üretiyor |
| MP-QA-004 | P2 | Uygulama | Ana ekrandaki üç yönlendirme düğmesi çalışmıyor |
| MP-QA-005 | P2 | Uygulama | Ortak modal klavye/erişilebilirlik eksikleri |
| MP-QA-006 | P3 | Uygulama | Fiyat doğrulama mesajında bozuk Türkçe |
| MP-QA-007 | P2 | Test | Hesap regresyonu yeni rol kuralıyla çelişiyor |
| MP-QA-008 | P2 | Test | Katalog regresyonu eski envanter DOM yapısını bekliyor |
| MP-QA-009 | P2 | Test | İki eski UI paketi WMS ekranında workflow bileşeni bekliyor |
| MP-QA-010 | P2 | Test | Operasyon hazırlık testi yetkili fiyat/stock kuralını kullanmıyor |
| MP-QA-011 | P2 | Güvenlik riski | CSP inline event handler çalışmasına izin veriyor |
| MP-QA-012 | P3 | Uygulama | Mobil menü düğmesinin erişilebilir adı yok |

## MP-QA-001 · P1 · Siparişler arası stok tahsisi kapasiteyi aşıyor

- Tür / durum: **Uygulama — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [FulfillmentOrchestration.cs:493](../../src/MarketPilot.Web/Wms/FulfillmentOrchestration.cs#L493).
- Kanıt: [evidence/invariants-probe.json](evidence/invariants-probe.json).
- Tekrar üretme: `node tests/qa/run-probes.cjs invariants`: onaylı mock-main / TSH-EKR-S, satılabilir 30; ayrı kaynak kimlikli iki siparişin her biri 30 ister. İkisini paralel al ve iki adım ilerlet.
- Beklenen: Toplam etkin tahsis en fazla 30 olmalı; ikinci sipariş stok beklemeli/bloke edilmeli.
- Gerçek: Tahsis 60; her iki sipariş validated ve ardından wms-accepted. Yerel usedByWarehouseSku yalnız tek siparişi kapsıyor. Aynı WMS snapshot kullanıldığında başka iş akışlarının tahsisi düşülmüyor.
- Çözüm önerisi: Tenant+depo+varyant bazında ortak atomik tahsis/rezervasyon defteri; WMS kabulü ile işlenme/rezervasyon teyidini ayır; red/iptal/timeout/restart sonrası uzlaştır.
- Gerekli regresyon: Paralel ve ardışık 2–20 sipariş, kısmi stok, timeout ve restart; toplam etkin rezervasyon kapasiteyi aşmamalı. CanonicalStore rezervasyon kilidinin geçmesi bu ayrı fulfillment yolunu doğrulamaz.

## MP-QA-002 · P1 · Yedek başarılı görünürken yeni modüller kapsam dışı

- Tür / durum: **Uygulama — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [LocalReliability.cs:39](../../src/MarketPilot.Web/Local/LocalReliability.cs#L39).
- Kanıt: [evidence/backup-probe.json](evidence/backup-probe.json).
- Tekrar üretme: `node tests/qa/run-probes.cjs backup`: sentetik tenant, credential-reference, WMS inventory/fulfillment/logistics ve monitoring dosyalarıyla Create, Verify ve RestoreCheck çalıştır; arşiv manifestini oku.
- Beklenen: Gerekli modül dosyaları manifestte ve geri alma provasının kapsam kontrolünde olmalı.
- Gerçek: Verify.ok ve RestoreCheck.ok true; altı yeni dosya yok. RestoreCheck yalnız eski demo/catalog/store/queue/inbox alanlarını açıyor. Tenant üyelikleri ve WMS işlem geçmişi bu arşivden kurtarılamaz.
- Çözüm önerisi: Sürümlü, modül kayıtlı yedek manifesti; eksik gerekli dosyada fail-closed; tenant kapsamı, tutarlı snapshot ve ayrı anahtar kurtarma planı.
- Gerekli regresyon: Her modüle sentetik sentinel yaz, ayrı klasöre geri aç, kimlikleri/rezervasyonları/makbuzları karşılaştır; dosya eksikliği başarılı sayılmamalı.

## MP-QA-003 · P2 · Eksik CredentialReference.System HTTP 500 üretiyor

- Tür / durum: **Uygulama — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [TenantBoundary.cs:103](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L103).
- Kanıt: [evidence/targeted-results.json](evidence/targeted-results.json).
- Tekrar üretme: İzole ortamda admin oturumu ile PUT /api/security/connections/credentials, JSON `{}` gönder.
- Beklenen: Eksik alan için kontrollü 400; veri değişmemeli.
- Gerçek: System null iken ToUpperInvariant çağrılıyor; 500 genel hata. Sonraki health 200, süreç ayakta.
- Çözüm önerisi: Gerekli alanları herhangi bir erişim/normalizasyon öncesi doğrula; domain/DTO doğrulamasını tek yerde uygula.
- Gerekli regresyon: Her credential alanı için eksik/null/boş/yanlış tip ve yabancı tenant; 4xx, sır içermeyen cevap, aynı JSON dosyası.

## MP-QA-004 · P2 · Ana ekrandaki üç yönlendirme düğmesi çalışmıyor

- Tür / durum: **Uygulama — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [app.js:17](../../src/MarketPilot.Web/wwwroot/assets/app.js#L17).
- Kanıt: [evidence/targeted-results.json](evidence/targeted-results.json).
- Tekrar üretme: Genel Bakış: Satış Kanalları bölümündeki ok, Son Siparişler “Tümünü gör”, Son İşlemler oku tıklanır. Çağıran kod app.js:21.
- Beklenen: reports/orders/activity sayfalarına yönlendirme.
- Gerçek: btn action çift tırnaklı HTML attribute içine kaçırılmadan konuyor. `nav("orders")` DOM üzerinde `onclick="nav("` oluyor; üç gerçek tıklama Unexpected end of input veriyor.
- Çözüm önerisi: Inline string birleştirme yerine event listener / data-action kullan; action verisini güvenli kodla.
- Gerekli regresyon: Üç gerçek tıklama, doğru hash/ekran ve sıfır pageerror; node --check tek başına yeterli değil.

## MP-QA-005 · P2 · Ortak modal klavye/erişilebilirlik eksikleri

- Tür / durum: **Uygulama — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [app.js:140](../../src/MarketPilot.Web/wwwroot/assets/app.js#L140).
- Kanıt: [evidence/targeted-results.json](evidence/targeted-results.json).
- Tekrar üretme: Admin Kullanıcı & Roller ekranındaki gerçek localPasswordForm ile parola penceresini aç. Role/aria-modal ve activeElement kontrol et, Escape bas.
- Beklenen: Dialog semantiği, pencereye odak, Escape kapama ve açan kontrole odak dönüşü; Tab odağı pencere içinde kalmalı.
- Gerçek: role=dialog/aria-modal yok, odak içeride değil, Escape kapatmıyor. İlk üç kontrol ayrı ayrı başarısız. Tab döngüsü/ekran okuyucu tam testi yapılmadı.
- Çözüm önerisi: Ortak modal yöneticisine dialog adı/semantiği, odak taşıma/tutma/geri verme ve Escape ekle.
- Gerekli regresyon: Parola/maliyet/genel modal klavye testleri; ekran okuyucu ve tam WCAG kontrolü ayrıca.

## MP-QA-006 · P3 · Fiyat doğrulama mesajında bozuk Türkçe

- Tür / durum: **Uygulama — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [ErpPricingService.cs:84](../../src/MarketPilot.Web/Pricing/ErpPricingService.cs#L84).
- Kanıt: [evidence/targeted-results.json](evidence/targeted-results.json).
- Tekrar üretme: Admin POST /api/pricing/preview `{}`.
- Beklenen: Okunabilir Türkçe ürün/mağaza/ilan uyarısı.
- Gerçek: HTTP 400 metni: `?r?n, ma?aza ve ilan kimli?i gerekli.` Kaynak literalinde de aynı, terminal kodlama sorunu değil.
- Çözüm önerisi: UTF-8 mesajı düzelt, kullanıcıya gösterilen hata metinlerini kaynak dosyalarında denetle.
- Gerekli regresyon: API hata metni ve tarayıcı bildirimi Unicode olarak karşılaştırılmalı.

## MP-QA-007 · P2 · Hesap regresyonu yeni rol kuralıyla çelişiyor

- Tür / durum: **Test — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [accounts-http-checks.cjs:61](../../tests/accounts-http-checks.cjs#L61).
- Kanıt: [evidence/accounts-diagnostic.log](evidence/accounts-diagnostic.log).
- Tekrar üretme: Mevcut accounts-http-checks.cjs çalıştırılır; tanı kopyası yalnız JSON parse hatasına yol/durum bilgisi ekler, orijinal değişmez.
- Beklenen: Viewer credential-profili okuyamaz; test 403 ve boş gövdeyi desteklemeli.
- Gerçek: GET /api/integrations/credential-profiles viewer ile 403; test 200 bekliyor ve boş gövdeyi koşulsuz JSON okuyup SyntaxError ile duruyor. Yetki koruması çalışıyor.
- Çözüm önerisi: Onaylı rol matrisiyle beklenen durumu güncelle, API helper boş gövdeyi desteklesin; güvenliği gevşetme.
- Gerekli regresyon: Admin profili okur, read-only/operations göremez; sır yok; iki hesap ve sonraki UI kontrolleri tamamlanmalı.

## MP-QA-008 · P2 · Katalog regresyonu eski envanter DOM yapısını bekliyor

- Tür / durum: **Test — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [catalog-http-checks.cjs:17](../../tests/catalog-http-checks.cjs#L17).
- Kanıt: [evidence/catalog-http-checks.cjs.log](evidence/catalog-http-checks.cjs.log).
- Tekrar üretme: Mevcut catalog HTTP/UI paketinde inventory ekranına geç.
- Beklenen: Güncel WMS stok ekranı ve kaynak/depo alanları doğrulanmalı.
- Gerçek: Eski .ch-body .ty-business-table bekleniyor; WMS inventory bu katalog tablosunu kullanmıyor. Timeout ile sonraki senaryolara ulaşılamıyor.
- Çözüm önerisi: Güncel WMS ekranına göre işlevsel seçiciler kullan; ERP katalog kontrollerini ayrı tut.
- Gerekli regresyon: Mock stok okuma, kaynak zamanı, rezervasyon, satılabilir stok, farklar ve mobil; eski senaryoları silmeden yeni kurala uyarlama.

## MP-QA-009 · P2 · İki eski UI paketi WMS ekranında workflow bileşeni bekliyor

- Tür / durum: **Test — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [clothing-catalog-http-checks.cjs:17](../../tests/clothing-catalog-http-checks.cjs#L17).
- Kanıt: [evidence/clothing-catalog-http-checks.cjs.log](evidence/clothing-catalog-http-checks.cjs.log).
- Tekrar üretme: clothing-catalog-http-checks.cjs veya shared-workspace-http-checks.cjs:85 modül döngüsünü çalıştır; picking ardından labels/tracking/returns.
- Beklenen: Giyim kontrolleri korunarak WMS lojistik ekranları doğru modelle test edilmeli.
- Gerçek: clothing labels için [data-workflow="labels"] .wu-filter; shared wuState(module).result bekliyor. wms-logistics.js:47 bu ekranların renderını değiştiriyor. İki timeout tekrar üretildi. Dashboard raporu ayrı taze oturumda başarıyla yüklendi.
- Çözüm önerisi: Modül bazında güncel render sözleşmesine ayrılan testler; backend workspace ve WMS lojistik regresyonları ayrıştırılmalı.
- Gerekli regresyon: labels/tracking/returns WMS veri yolu; shared-workspace akışının timeout sonrası kalan tüm adımları yeniden çalışmalı.

## MP-QA-010 · P2 · Operasyon hazırlık testi yetkili fiyat/stock kuralını kullanmıyor

- Tür / durum: **Test — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [operational-preparation-http-checks.cjs:37](../../tests/operational-preparation-http-checks.cjs#L37).
- Kanıt: [evidence/operational-preparation-http-checks.cjs.log](evidence/operational-preparation-http-checks.cjs.log).
- Tekrar üretme: Mevcut operational-preparation-http-checks.cjs çalıştır; ilk yerel sipariş hazırlığını incele.
- Beklenen: Yetkili ERP fiyatı fixture ile açıkça bilinir olmalı; sipariş fiziksel WMS stoğunu değiştirmemeli.
- Gerçek: İlk sipariş 400 dönüyor, test 200 bekliyor: aktif/fiyatlı/yeterli stok gerekliliği. İleride testin eski 10→3 fiziksel stok beklentileri de güncel kaynak sahipliğiyle çelişiyor. Restore-check satırına ulaşılmıyor.
- Çözüm önerisi: Doğrulanmış mock fiyat listesi/politikası fixture kullan; rezervasyon ile fiziksel stok beklentisini ayır.
- Gerekli regresyon: Fiyat bilinmiyor 4xx, bilinen fiyatla sipariş ve replay, fiziksel stok değişmezliği; partial cancel/return/split ve restore-check sonuna kadar.

## MP-QA-011 · P2 · CSP inline event handler çalışmasına izin veriyor

- Tür / durum: **Güvenlik riski — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [SecurityHardening.cs:61](../../src/MarketPilot.Web/Local/SecurityHardening.cs#L61).
- Kanıt: [evidence/static-analysis.json](evidence/static-analysis.json).
- Tekrar üretme: CSP response header ve inline onclick/onsubmit kullanan frontend kaynaklarını incele.
- Beklenen: Üretim hedefi: script-src-attr none, event listener ve sıkı script politikası.
- Gerçek: script-src-attr unsafe-inline mevcut; klasik script tag için script-src-elem self koruması var. Mevcut XSS testleri geçti; sömürülebilir yeni XSS gösterilmedi. Bu bir savunma katmanı açığı/risk, kanıtlanmış hesap ele geçirme değil.
- Çözüm önerisi: Inline handler geçişi ve CSP sıkılaştırması; geçici koruma kapatılmamalı. Eski tarayıcı CSP fallback davranışı ayrıca doğrulanmalı.
- Gerekli regresyon: Stored/reflected DOM XSS fixture, inline event saldırısı, tüm mevcut düğmeler; Chrome/Firefox/WebKit üzerinde sıkı CSP.

## MP-QA-012 · P3 · Mobil menü düğmesinin erişilebilir adı yok

- Tür / durum: **Uygulama — açık**. Bu denetimde düzeltilmedi.
- Kaynak: [app.js:18](../../src/MarketPilot.Web/wwwroot/assets/app.js#L18).
- Kanıt: [evidence/ui-performance-results.json](evidence/ui-performance-results.json).
- Tekrar üretme: 390 veya 768px genişlikte herhangi bir ekranı aç; .mobile-toggle düğmesinin textContent, aria-label, aria-labelledby ve title alanlarını kontrol et.
- Beklenen: Ekran okuyucu menü aç/kapat düğmesinin amacını duyurmalı; açık/kapalı durumu bildirilmeli.
- Gerçek: Düğme yalnız SVG ikon içeriyor; erişilebilir ad ve aria-expanded yok. 42 ekranın tablet/mobil taramasında aynı adsız düğme görüldü; desktopta gizli.
- Çözüm önerisi: Menüyü aç/kapat aria-label, aria-expanded ve aria-controls ekle; görünür menü/overlay klavye davranışını doğrula.
- Gerekli regresyon: Tablet/mobil menü role/name ile bulunabilmeli, klavyeyle açılıp kapanmalı; genişlik ve Escape/odak kontrolü.
