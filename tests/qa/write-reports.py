from pathlib import Path
import json,collections
root=Path(__file__).resolve().parents[2];out=root/'docs/qa';ev=out/'evidence'
read=lambda name:json.loads((ev/name).read_text(encoding='utf-8'))
baseline=read('source-baseline.json');inventory=read('inventory.json');runs=read('test-results.json');ui=read('ui-performance-results.json');target=read('targeted-results.json');performance=read('performance-probe.json');static=read('static-analysis.json')
def write(name,text):
 (out/name).write_text(text.strip()+'\n',encoding='utf-8')
def table(headers,rows):
 def cell(value):return str(value).replace('|',' / ').replace('\n',' ')
 return '| '+' | '.join(headers)+' |\n| '+' | '.join(['---']*len(headers))+' |\n'+'\n'.join('| '+' | '.join(cell(c) for c in row)+' |' for row in rows)
def source(file,line):return f'[{Path(file).name}:{line}](../../{file}#L{line})'
def evidence(name):return f'[evidence/{name}](evidence/{name})'
stamp=f"30 Ekim 2026 · HEAD `{baseline['head'][:7]}` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74"
legend='**Durumlar:** Tamamlandı = belirtilen yerel kapsam doğrulandı; Kısmi = akışın yalnız bir bölümü; Demo = mock/fixture; Eksik = mevcut değil; Hatalı = tekrar üretildi; Doğrulanmadı = test edilmedi. Tamamlandı, canlı sağlayıcı sertifikasyonu anlamına gelmez.'
bugs=[
 ('MP-QA-001','P1','Uygulama','Siparişler arası stok tahsisi kapasiteyi aşıyor','src/MarketPilot.Web/Wms/FulfillmentOrchestration.cs',493,'invariants-probe.json',
  '`node tests/qa/run-probes.cjs invariants`: onaylı mock-main / TSH-EKR-S, satılabilir 30; ayrı kaynak kimlikli iki siparişin her biri 30 ister. İkisini paralel al ve iki adım ilerlet.',
  'Toplam etkin tahsis en fazla 30 olmalı; ikinci sipariş stok beklemeli/bloke edilmeli.','Tahsis 60; her iki sipariş validated ve ardından wms-accepted. Yerel usedByWarehouseSku yalnız tek siparişi kapsıyor. Aynı WMS snapshot kullanıldığında başka iş akışlarının tahsisi düşülmüyor.',
  'Tenant+depo+varyant bazında ortak atomik tahsis/rezervasyon defteri; WMS kabulü ile işlenme/rezervasyon teyidini ayır; red/iptal/timeout/restart sonrası uzlaştır.',
  'Paralel ve ardışık 2–20 sipariş, kısmi stok, timeout ve restart; toplam etkin rezervasyon kapasiteyi aşmamalı. CanonicalStore rezervasyon kilidinin geçmesi bu ayrı fulfillment yolunu doğrulamaz.'),
 ('MP-QA-002','P1','Uygulama','Yedek başarılı görünürken yeni modüller kapsam dışı','src/MarketPilot.Web/Local/LocalReliability.cs',39,'backup-probe.json',
  '`node tests/qa/run-probes.cjs backup`: sentetik tenant, credential-reference, WMS inventory/fulfillment/logistics ve monitoring dosyalarıyla Create, Verify ve RestoreCheck çalıştır; arşiv manifestini oku.',
  'Gerekli modül dosyaları manifestte ve geri alma provasının kapsam kontrolünde olmalı.','Verify.ok ve RestoreCheck.ok true; altı yeni dosya yok. RestoreCheck yalnız eski demo/catalog/store/queue/inbox alanlarını açıyor. Tenant üyelikleri ve WMS işlem geçmişi bu arşivden kurtarılamaz.',
  'Sürümlü, modül kayıtlı yedek manifesti; eksik gerekli dosyada fail-closed; tenant kapsamı, tutarlı snapshot ve ayrı anahtar kurtarma planı.',
  'Her modüle sentetik sentinel yaz, ayrı klasöre geri aç, kimlikleri/rezervasyonları/makbuzları karşılaştır; dosya eksikliği başarılı sayılmamalı.'),
 ('MP-QA-003','P2','Uygulama','Eksik CredentialReference.System HTTP 500 üretiyor','src/MarketPilot.Web/Local/TenantBoundary.cs',103,'targeted-results.json',
  'İzole ortamda admin oturumu ile PUT /api/security/connections/credentials, JSON `{}` gönder.',
  'Eksik alan için kontrollü 400; veri değişmemeli.','System null iken ToUpperInvariant çağrılıyor; 500 genel hata. Sonraki health 200, süreç ayakta.',
  'Gerekli alanları herhangi bir erişim/normalizasyon öncesi doğrula; domain/DTO doğrulamasını tek yerde uygula.',
  'Her credential alanı için eksik/null/boş/yanlış tip ve yabancı tenant; 4xx, sır içermeyen cevap, aynı JSON dosyası.'),
 ('MP-QA-004','P2','Uygulama','Ana ekrandaki üç yönlendirme düğmesi çalışmıyor','src/MarketPilot.Web/wwwroot/assets/app.js',17,'targeted-results.json',
  'Genel Bakış: Satış Kanalları bölümündeki ok, Son Siparişler “Tümünü gör”, Son İşlemler oku tıklanır. Çağıran kod app.js:21.',
  'reports/orders/activity sayfalarına yönlendirme.','btn action çift tırnaklı HTML attribute içine kaçırılmadan konuyor. `nav("orders")` DOM üzerinde `onclick="nav("` oluyor; üç gerçek tıklama Unexpected end of input veriyor.',
  'Inline string birleştirme yerine event listener / data-action kullan; action verisini güvenli kodla.',
  'Üç gerçek tıklama, doğru hash/ekran ve sıfır pageerror; node --check tek başına yeterli değil.'),
 ('MP-QA-005','P2','Uygulama','Ortak modal klavye/erişilebilirlik eksikleri','src/MarketPilot.Web/wwwroot/assets/app.js',140,'targeted-results.json',
  'Admin Kullanıcı & Roller ekranındaki gerçek localPasswordForm ile parola penceresini aç. Role/aria-modal ve activeElement kontrol et, Escape bas.',
  'Dialog semantiği, pencereye odak, Escape kapama ve açan kontrole odak dönüşü; Tab odağı pencere içinde kalmalı.','role=dialog/aria-modal yok, odak içeride değil, Escape kapatmıyor. İlk üç kontrol ayrı ayrı başarısız. Tab döngüsü/ekran okuyucu tam testi yapılmadı.',
  'Ortak modal yöneticisine dialog adı/semantiği, odak taşıma/tutma/geri verme ve Escape ekle.',
  'Parola/maliyet/genel modal klavye testleri; ekran okuyucu ve tam WCAG kontrolü ayrıca.'),
 ('MP-QA-006','P3','Uygulama','Fiyat doğrulama mesajında bozuk Türkçe','src/MarketPilot.Web/Pricing/ErpPricingService.cs',84,'targeted-results.json',
  'Admin POST /api/pricing/preview `{}`.',
  'Okunabilir Türkçe ürün/mağaza/ilan uyarısı.','HTTP 400 metni: `?r?n, ma?aza ve ilan kimli?i gerekli.` Kaynak literalinde de aynı, terminal kodlama sorunu değil.',
  'UTF-8 mesajı düzelt, kullanıcıya gösterilen hata metinlerini kaynak dosyalarında denetle.',
  'API hata metni ve tarayıcı bildirimi Unicode olarak karşılaştırılmalı.'),
 ('MP-QA-007','P2','Test','Hesap regresyonu yeni rol kuralıyla çelişiyor','tests/accounts-http-checks.cjs',61,'accounts-diagnostic.log',
  'Mevcut accounts-http-checks.cjs çalıştırılır; tanı kopyası yalnız JSON parse hatasına yol/durum bilgisi ekler, orijinal değişmez.',
  'Viewer credential-profili okuyamaz; test 403 ve boş gövdeyi desteklemeli.','GET /api/integrations/credential-profiles viewer ile 403; test 200 bekliyor ve boş gövdeyi koşulsuz JSON okuyup SyntaxError ile duruyor. Yetki koruması çalışıyor.',
  'Onaylı rol matrisiyle beklenen durumu güncelle, API helper boş gövdeyi desteklesin; güvenliği gevşetme.',
  'Admin profili okur, read-only/operations göremez; sır yok; iki hesap ve sonraki UI kontrolleri tamamlanmalı.'),
 ('MP-QA-008','P2','Test','Katalog regresyonu eski envanter DOM yapısını bekliyor','tests/catalog-http-checks.cjs',17,'catalog-http-checks.cjs.log',
  'Mevcut catalog HTTP/UI paketinde inventory ekranına geç.',
  'Güncel WMS stok ekranı ve kaynak/depo alanları doğrulanmalı.','Eski .ch-body .ty-business-table bekleniyor; WMS inventory bu katalog tablosunu kullanmıyor. Timeout ile sonraki senaryolara ulaşılamıyor.',
  'Güncel WMS ekranına göre işlevsel seçiciler kullan; ERP katalog kontrollerini ayrı tut.',
  'Mock stok okuma, kaynak zamanı, rezervasyon, satılabilir stok, farklar ve mobil; eski senaryoları silmeden yeni kurala uyarlama.'),
 ('MP-QA-009','P2','Test','İki eski UI paketi WMS ekranında workflow bileşeni bekliyor','tests/clothing-catalog-http-checks.cjs',17,'clothing-catalog-http-checks.cjs.log',
  'clothing-catalog-http-checks.cjs veya shared-workspace-http-checks.cjs:85 modül döngüsünü çalıştır; picking ardından labels/tracking/returns.',
  'Giyim kontrolleri korunarak WMS lojistik ekranları doğru modelle test edilmeli.','clothing labels için [data-workflow="labels"] .wu-filter; shared wuState(module).result bekliyor. wms-logistics.js:47 bu ekranların renderını değiştiriyor. İki timeout tekrar üretildi. Dashboard raporu ayrı taze oturumda başarıyla yüklendi.',
  'Modül bazında güncel render sözleşmesine ayrılan testler; backend workspace ve WMS lojistik regresyonları ayrıştırılmalı.',
  'labels/tracking/returns WMS veri yolu; shared-workspace akışının timeout sonrası kalan tüm adımları yeniden çalışmalı.'),
 ('MP-QA-010','P2','Test','Operasyon hazırlık testi yetkili fiyat/stock kuralını kullanmıyor','tests/operational-preparation-http-checks.cjs',37,'operational-preparation-http-checks.cjs.log',
  'Mevcut operational-preparation-http-checks.cjs çalıştır; ilk yerel sipariş hazırlığını incele.',
  'Yetkili ERP fiyatı fixture ile açıkça bilinir olmalı; sipariş fiziksel WMS stoğunu değiştirmemeli.','İlk sipariş 400 dönüyor, test 200 bekliyor: aktif/fiyatlı/yeterli stok gerekliliği. İleride testin eski 10→3 fiziksel stok beklentileri de güncel kaynak sahipliğiyle çelişiyor. Restore-check satırına ulaşılmıyor.',
  'Doğrulanmış mock fiyat listesi/politikası fixture kullan; rezervasyon ile fiziksel stok beklentisini ayır.',
  'Fiyat bilinmiyor 4xx, bilinen fiyatla sipariş ve replay, fiziksel stok değişmezliği; partial cancel/return/split ve restore-check sonuna kadar.'),
 ('MP-QA-011','P2','Güvenlik riski','CSP inline event handler çalışmasına izin veriyor','src/MarketPilot.Web/Local/SecurityHardening.cs',61,'static-analysis.json',
  'CSP response header ve inline onclick/onsubmit kullanan frontend kaynaklarını incele.',
  'Üretim hedefi: script-src-attr none, event listener ve sıkı script politikası.','script-src-attr unsafe-inline mevcut; klasik script tag için script-src-elem self koruması var. Mevcut XSS testleri geçti; sömürülebilir yeni XSS gösterilmedi. Bu bir savunma katmanı açığı/risk, kanıtlanmış hesap ele geçirme değil.',
  'Inline handler geçişi ve CSP sıkılaştırması; geçici koruma kapatılmamalı. Eski tarayıcı CSP fallback davranışı ayrıca doğrulanmalı.',
  'Stored/reflected DOM XSS fixture, inline event saldırısı, tüm mevcut düğmeler; Chrome/Firefox/WebKit üzerinde sıkı CSP.'),
 ('MP-QA-012','P3','Uygulama','Mobil menü düğmesinin erişilebilir adı yok','src/MarketPilot.Web/wwwroot/assets/app.js',18,'ui-performance-results.json',
  '390 veya 768px genişlikte herhangi bir ekranı aç; .mobile-toggle düğmesinin textContent, aria-label, aria-labelledby ve title alanlarını kontrol et.',
  'Ekran okuyucu menü aç/kapat düğmesinin amacını duyurmalı; açık/kapalı durumu bildirilmeli.',
  'Düğme yalnız SVG ikon içeriyor; erişilebilir ad ve aria-expanded yok. 42 ekranın tablet/mobil taramasında aynı adsız düğme görüldü; desktopta gizli.',
  'Menüyü aç/kapat aria-label, aria-expanded ve aria-controls ekle; görünür menü/overlay klavye davranışını doğrula.',
  'Tablet/mobil menü role/name ile bulunabilmeli, klavyeyle açılıp kapanmalı; genişlik ve Escape/odak kontrolü.'),
]
header=f'# MarketPilot proje denetimi\n\n{stamp}\n\n'
write('PROJECT_AUDIT.md',header+f'''
Karar: **canlı faz için NO-GO**. Yerel mock geliştirme ve hata düzeltme devam edebilir. Uygulama kaynakları bu denetimde düzeltilmedi; yalnız test altyapısı ve bu raporlar eklendi. Önceden değişmiş dosyalar korundu.

## Kapsam ve kanıtın sınırı

Güncel dosyalar, derleme, fixture testleri ve izole süreçler esas alındı; eski raporlar sonuç kaynağı olarak kullanılmadı. {len(inventory['files'])} uygulama dosyası, {static['totalSourceLines']:,} kaynak satırı, {len(inventory['types'])} tür bildirimi, {len(inventory['endpoints'])} endpoint bildirimi, {len(inventory['groups'])} route grubu; mevcut 24 console test projesi ve 36 üst seviye kontrol betiği envanteri çıkarıldı. Tür sayısı partial tanımları da içerir. Endpoint sayısı dinamik Trendyol işlem kataloğunu ve genişleyen route kombinasyonlarını ayrı endpoint olarak saymaz.

{legend}

173 dosyanın tamamı dosya/tür/endpoint/işaret envanterinde yer alır. Modül kaynakları iş kuralları ve test kanıtlarıyla birlikte incelendi. Bu, her satır için biçimsel doğrulama veya her endpoint × rol × payload kombinasyonu testi değildir. Bu kombinasyonların tümüne kapsam verilemedi; doğrulanmayanlar aşağıdaki raporlarda açıkça belirtilir.

## Mimari

ASP.NET Core 8 tek web projesi ve DI servisleri; statik HTML/CSS/JavaScript, frontend framework yok. Kalıcılık tenant klasörlerine ayrılmış JSON dosyaları; ilişkisel DB/EF/Npgsql yok. Servisler process içi lock/semaphore ve dosya değişimi kullanıyor. DataDirectoryLock aynı veri klasörünü ikinci süreçte açmayı engelliyor. Bu yapı yerel tek süreç içindir; yatay ölçek ve çok servisli işlem garantisi olarak sunulamaz.

```mermaid
flowchart LR
  ERP[Mock Nebim provider] --> C[ERP katalog ve fiyat projeksiyonları]
  M[Altı kanal adaptörü / fixture] --> O[OrderSynchronization ve webhook inbox]
  O --> E[EnterpriseSync kalıcı işleri / IntegrationOrchestrator]
  E --> F[Fulfillment policy ve durum makinesi]
  F --> W[Mock WMS]
  W --> I[Çok depolu stok ve lojistik]
  C --> S[Mevcut StockOutbox / yayın hazırlığı]
  I --> S
  S --> M
```

Şema hedef bağımlılıkları gösterir; her ok canlı veya tam atomik değildir. Özellikle fulfillment tahsisleri ortak rezervasyon defterine bağlanmadığı için MP-QA-001 mevcut.

## Klasörler ve sorumluluklar

{table(['Alan','Gerçek rol / durum'],[
 ('src/MarketPilot.Web/Program.cs','DI, middleware sırası, temel ürün/sipariş/ayar/export/health uçları'),
 ('Catalog','ERP salt okunur katalog, fark/uygulama, mevcut ilan eşleştirmesi, kategori ve ürün sahipliği'),
 ('Contracts','17 ortak model, kimlik/sürüm/idempotency, manuel eşleştirme ve türetilmiş ATS'),
 ('Enterprise/Nebim','INebimErpProvider ve MockNebimProvider; yerel uygulama API’leri; native Nebim adaptörü yok'),
 ('Enterprise','Mevcut bridge, plan/zamanlayıcı, kalıcı iş, orchestrator, ERP aktarım hazırlığı ve izleme'),
 ('Pricing','Yetkili ERP listesi, KDV/para birimi/masraf/kampanya/onay/preview; gerçek fiyat gönderimi engelli'),
 ('Wms','Bağımsız provider sözleşmesi, mock, çok depo, fulfillment, sevkiyat/iade/etiket yetkisi'),
 ('Integrations','Altı native adaptör, kimlik profilleri, ortak mapper/capability; demo sağlayıcıları ve central-stock'),
 ('Synchronization / Webhooks','Sipariş okuyucu, kalıcı StockOutbox, imzalı inbox ve worker'),
 ('Local','Cookie/rol/tenant, SSRF/DNS hedef koruması, dosya erişimi, log masking, yedek ve recovery'),
 ('Workspace / Analytics','Yerel operasyon talepleri, belge/refund taslakları, rapor, maliyet, aktivite'),
 ('wwwroot/assets','30 JS dosyası; render fonksiyonlarının katmanlı override’ları, tablo/filtre/formlar'),
 ('tests','23 Checks projesi + WorkerIsolationAudit, 36 mevcut betik; tests/qa denetim testleri'),
 ('.github/workflows / Dockerfile / compose.yaml / Caddyfile','CI tanımı ve TLS/reverse proxy dağıtım hazırlığı; gerçek deploy yapılmadı')])}

## Mimari sorunlar ve sadeleştirme

- **Birden çok stok/rezervasyon temsili:** CanonicalStore, OperationalInventory, central-stock, WmsInventoryService ve FulfillmentOrchestration farklı yollar. Birinin lock testinin geçmesi diğerini güvenli yapmıyor (MP-QA-001).
- **İki IWmsProvider:** Enterprise/EnterpriseProviders.cs:8 mevcut özel bridge; Wms/Contracts.cs bağımsız provider. Namespace ayrımı derlemeyi sağlıyor; isim ve yetenek sınırı bakım maliyetini artırıyor. Adaptasyon ve tek tahsis otoritesi gerekli.
- **Kuyruklar:** orchestrator ikinci paralel yürütücü kurmuyor, EnterpriseSync işlerini kullanıyor. Sistemde önceden bulunan webhook inbox, stock outbox ve demo jobs farklı amaçlarla yaşamaya devam ediyor. Uçtan uca atomiklik ayrı JSON dosyaları arasında yok; idempotency/uzlaştırmayla yönetiliyor.
- **Frontend override zinciri:** app.js, catalog, listing-filters, operations, analytics, workflow ve WMS modülleri global fonksiyonları değiştiriyor. Eski seçicilere bağlı 3 UI paketi bu yüzden duruyor. Modül router ve ortak event/modal katmanı önerilir.
- **JSON maliyeti:** tam snapshot/serileştirme ve process içi kilitler; arşivleme/retention ve büyük kuyruk/rapor indeksi üretim ölçeğinde doğrulanmış değil.
- **Audit:** tenant kontrollü işlem izleri ve maskelenmiş olaylar var; değiştirilemez harici audit, metrik/alert SLO ve DR tatbikatı üretim fazına kalıyor.

## Gereksiz / eski alanlar

app.js eski placeholder, openBarcode, yerel shipping/listing fonksiyonları taşıyor; sonraki JS dosyaları birçok aktif ekranı override ediyor. Bunlar statik adaylardır: dış tüketici/çalışma anı referansı ölçülmeden “tamamen kullanılmıyor” denilip silinmemeli. Program.cs eski ürün yazma route’ları kasıtlı 405/409 uyumluluk koruması; gereksiz endpoint değildir. Doğrudan UI çağrısı olmayan provider, webhook ve worker API’leri de dış/internal kullanım nedeniyle ölü kod sayılmadı.

Tek literal TODO: Nebim/Contracts.cs:17, doğrulanmış native adaptör sözleşmesini bekliyor. Kaynak taramasında literal FIXME/HACK/NotImplementedException bulunmadı. Bu sayı işlevsel eksik olmadığı anlamına gelmez; capability/NotSupported ve mock alanları ayrıca değerlendirildi.

## Kanıtlar

- {evidence('inventory.json')}: bütün dosyalar, türlerin file:line listesi, route bildirimleri, JS API literalleri ve test projeleri.
- {evidence('source-baseline.json')} / {evidence('source-final.json')}: HEAD, başlangıç çalışma ağacı ve SHA-256 karşılaştırması. Kaynak değişimi: **0**.
- {evidence('static-analysis.json')}, {evidence('test-results.json')}, [özellik matrisi](FEATURE_MATRIX.md), [hata kaydı](BUG_REGISTER.md).

Testin ürettiği üç resilience PNG’si evidence/resilience altında saklandı; başlangıçta temiz olan artifacts/resilience dosyaları eski baytlarına geri kondu. Kullanıcı veri klasörleri açılmadı/değiştirilmedi. Üretim veya ana uygulama servisi başlatılmadı; yalnız geçici test süreçleri çalıştırıldı.
''')
features=[
 ('Ortak veri sözleşmeleri','Tamamlandı / Demo','17 model, sürüm/replay/manuel kuyruk; native provider sözleşmesi değil','CanonicalChecks, canonical HTTP'),
 ('SKU/barkod/varyant kimliği','Tamamlandı / Demo','Çelişkiler incelemede, ada göre birleştirme yok','CanonicalChecks, CatalogChecks'),
 ('Nebim ürün/özellik/görsel okuma','Demo','Mock provider ve ERP projeksiyonu; native Nebim yok','NebimChecks, ErpReadModelChecks, ERP HTTP'),
 ('Ürün oluşturma/silme/serbest ana veri','Tamamlandı (engelleme)','Ürün API 405/409, ERP metadata salt okunur; Trendyol create route engelli','erp-read-model HTTP'),
 ('ERP değişiklik farkı / kaybolan kayıt','Tamamlandı / Demo','Eşleme korunur, kayıp kaynak incelemede, fiziksel silme yok','ErpReadModelChecks'),
 ('Kategori / mevcut ilan eşleştirme','Kısmi','Giyim kategori UI/API ve alias eşleme çalışıyor; gerçek kanal kategori zorunlulukları doğrulanmadı','CatalogChecks; stock-actions UI; eski UI paketleri FAIL'),
 ('ERP yetkili fiyat listesi','Tamamlandı / Demo','Kaynak listesi/kanal/KDV/kuralsız kur çevrimi engeli','PricingChecks, pricing HTTP'),
 ('Fiyat onayı / versiyon / kampanya','Tamamlandı / Demo','Masraf tabanı, sınır, expiry ve stale preview; MP-QA-006 mesajı','PricingChecks'),
 ('Gerçek fiyat gönderimi','Eksik / kasıtlı kapalı','RealPriceWrites.Deny; mock yayın var','PricingChecks, AccountWriteChecks'),
 ('WMS provider sözleşmesi','Demo','Capabilities, gecikme/hata/restart; native provider yok','WmsChecks, fulfillment HTTP'),
 ('Çok depo stok ve ATS','Tamamlandı / Demo','Depolar, rezervasyon/hasarlı/bloke/transfer, limits, kanal yönetimli stok','wms-inventory HTTP'),
 ('Fulfillment stok tahsis kapasitesi','Hatalı','Siparişler arası kapasite aşılabiliyor','MP-QA-001, invariants-probe'),
 ('ERP/WMS yazma sırası','Tamamlandı / Demo','Onaylı policy; kabul/işlenme ayrımı, belirsizde sorgu','WmsChecks, orchestrator HTTP'),
 ('Kısmi sevk / çok paket / depo bölme','Demo','Mock state machine ve negatifler; kapasite kusuru nedeniyle uçtan uca hazır değil','WmsChecks, WmsLogisticsChecks'),
 ('Kargo / takip / etiket yetkisi','Demo','Kanal durum eşlemesi ve yetkili etiket bekleme; sahte belge üretilmiyor','wms-logistics HTTP'),
 ('İade / kalite kontrol / restock','Demo','WMS onayı, kendi stoğunu doğrudan artırmama; gerçek refund yok','WmsLogisticsChecks'),
 ('ERP sipariş aktarım hazırlığı','Tamamlandı / Demo','Kanal/cari/varyant/vergi/tutar, kabul referansı ve sorgu; native yazma yok','ErpTransferChecks, orchestrator HTTP'),
 ('Merkezi olay orkestrasyonu','Kısmi / Demo','12 olay türü; her olay×hedef yazma destekli değil, belirsiz yazma sorgulanıyor','OrchestratorChecks, EnterpriseSync.Integration'),
 ('Sipariş çekme / webhook / replay','Kısmi','Native reader ve inbox; sahte HTTP ile doğrulandı, canlı teslimatlar yok','SynchronizationChecks, webhook HTTP'),
 ('Altı kanal read/mapper/capability','Kısmi','Native adaptör + fixtures; kanal başına durum kapsamı farklı','ChannelContractChecks ve kanal paketleri'),
 ('Kalıcı outbox / retry / worker','Tamamlandı (yerel)','Restart, hata izolasyonu, backoff/receipt; dağıtık atomiklik yok','WorkerIsolationAudit, synchronization HTTP'),
 ('Tenant / oturum / altı rol','Tamamlandı (test edilen sınırlar)','Backend işletme bağlamı, yatay/dikey ihlal kontrolleri','tenant-security, tenant-isolation, LocalReliabilityChecks'),
 ('Secret referans / hedef koruması','Kısmi','JSON secret reddi, masking, SSRF/DNS, credentials {} 500','SecurityChecks; MP-QA-003'),
 ('Yedek ve geri alma','Hatalı / Kısmi','AES-GCM/hash doğrulama var; altı yeni modül eksik','MP-QA-002'),
 ('Rapor / maliyet / mutabakat','Kısmi','Yerel kayıt/hesaplamalar ve ERP tutar kontrolü; tüm native finans hareketleri değil','analytics-system HTTP, ErpTransferChecks'),
 ('Fatura / e-Fatura / ödeme','Kısmi / Demo','Taslak ve referans; GİB/e-fatura sağlayıcısı, gerçek ödeme/iade servisi yok','WorkspaceHub, OperationalService'),
 ('Frontend genel gezinme','Kısmi / Hatalı','42 ekran ×3 genişlik açıldı; dashboard düğmeleri ve modal kusuru','extended UI, targeted-results'),
 ('CI/CD / Docker / HTTPS','Kısmi','CI tanımı, Compose statik doğrulama, public proxy header testleri; TLS yayını ve image build çalıştırılmadı','deployment HTTP; compose direct'),
 ('Gerçek DB / migration / HA / DR','Eksik (ertelenmiş)','Kullanıcı sınırına uygun olarak oluşturulmadı; canlı faz gereksinimi','Kaynak ve paket envanteri')]
write('FEATURE_MATRIX.md',f'# Özellik matrisi\n\n{stamp}\n\n{legend}\n\n'+table(['Özellik','Durum','Sınır / bulgu','Kanıt'],features)+'\n\nBaşarılı yerel testler ürünün tamamlanma yüzdesi değildir. Gerçek API uyumluluğu, üretim kapasitesi ve veri tabanı dayanıklılığı bu matristen türetilemez.\n')

suite_rows=[(r['name'],r['status'],f"{r['durationMs']/1000:.2f}",f"[log](evidence/{r['log']})") for r in runs['results']]
testtypes=[
 ('Unit / iş kuralı / regresyon','Çalıştırıldı','23 console Checks projesi PASS; bunların bir kısmı integration/contract kontrolleri de içeriyor. Framework bazında ayrı case/branch sayacı yok.'),
 ('HTTP integration / API / contract','Çalıştırıldı','36 mevcut betiğin tamamı denendi; altı kanal native fixture ve sahte HTTP testleri. 5 betik FAIL.'),
 ('E2E / smoke / sanity / functional','Çalıştırıldı','Mock katalog, fiyat, sipariş→ERP/WMS, outbox/webhook, mapping/Ignore UI; canlı değil.'),
 ('Negative / boundary / data integrity','Çalıştırıldı','Yeni 21 API kontrolü 20 PASS/1 FAIL; null, {}, malformed, 9MiB header, origin ve health. Mevcut unit sınır testleri de var.'),
 ('Concurrency / race','Çalıştırıldı','CanonicalStore atomik rezervasyon PASS; yeni fulfillment çapraz sipariş kapasite FAIL (30→60).'),
 ('Idempotency / sırasız / source-version','Çalıştırıldı','Canonical, synchronization, ERP transfer, stock outbox, WMS logistics ve orchestrator paketleri.'),
 ('UI / Playwright / responsive','Çalıştırıldı','42 güncel menü ekranı × 1440/768/390 =126 açılış; 0 sayfa taşması, taramada 0 pageerror. Gerçek dashboard tıklamasında 3 hata ayrıca doğrulandı.'),
 ('Accessibility','Kısmi çalıştırıldı','DOM semantiği, isim/etiket heuristiği ve gerçek parola modalı odak/Escape. Tam WCAG/axe/ekran okuyucu testi yok.'),
 ('Browser compatibility','Kısmi','Yalnız Chrome 156. Firefox ve WebKit binary yok; Edge/Safari cihazı testi yok.'),
 ('Authentication / authorization / tenant','Çalıştırıldı','Yanlış/doğru parola, oturum yenilenmesi/iptali, yatay/dikey rol ihlali, header/query/body tenant ve dosya/export/log sınırları.'),
 ('Security / nonfunctional','Çalıştırıldı / sınırlı','SSRF/DNS/URL, CSRF/CORS/CSP/header, webhook imza/replay, secret/PII masking, CSV injection. Tam penetrasyon testi değil.'),
 ('Queue / worker / retry / recovery','Çalıştırıldı','23 ilgili birim kontrolü içinde ve HTTP paketleri; ayrıca tenant-isolation ve worker-isolation audit PASS.'),
 ('Backup / restore','Çalıştırıldı / FAIL','Şifre/hash/tamper pozitifleri geçti. Yeni probe Create/Verify/RestoreCheck ok iken 6 modülün eksikliğini kanıtlıyor. Eski operasyon paketinin restore satırı erken FAIL nedeniyle yürütülmedi.'),
 ('Performance / load / controlled stress','Çalıştırıldı / sınırlı','100/1k/10k sentetik arama; concurrency 1/4/8, toplam124 HTTP; tavan512MiB. Kuyruk tavan kapasitesi ölçülmedi.'),
 ('Controlled soak','Kısa smoke','30sn159 HTTP/0 hata. Saatler/günler boyu dayanıklılık veya bellek sızıntısı kanıtı değil.'),
 ('Dependency vulnerability','Sınırlı','npm audit --offline cache sonucu0; çevrim içi advisory güncelliği yok. NuGet PackageReference0; framework/container CVE taraması yok.'),
 ('CI/CD','Kısmi','Yerel build/suite ve Compose config; GitHub runner, Linux Chromium, Docker build/deploy/TLS yürütülmedi.'),
 ('Static analysis','Çalıştırıldı / sınırlı','173 dosya envanteri,30 JS syntax PASS, .NET build0 warning/error. SAST/taint/Sonar ve satır/branch coverage yok.')]
write('ALL_TEST_RESULTS.md',f'''# Bütün test sonuçları

{stamp}

## Sayım yöntemi

Ana tur: **63 komut = 58 PASS / 5 FAIL / 0 atlandı**. Bu toplam 61 mevcut test paketi (56 PASS / 5 FAIL), 1 web build ve 1 dış bağlantı engeli probe içerir. Mevcut test paketleri: 23 .NET Checks +36 betik+tenant-isolation-audit+WorkerIsolationAudit. Tek bir console paketi çok sayıda assert içeriyor; bunları ölçülmüş tekil test sayısı gibi toplamıyoruz.

Yeni kontroller ayrı ölçek: **29 tanımlı doğruluk kontrolü =20 PASS/9 FAIL**: 21 API (20/1), fulfillment invariant (0/1), yedek kapsamı (0/1), üç dashboard tıklaması (0/3), üç modal semantik/odak/Escape kontrolü (0/3). Bunlar 63 paketle toplanıp yanıltıcı “92 test” oranı üretilmedi.

Ek ölçümler:126 ekran/viewport gözlemi,30 JS syntax,3 veri boyutu ×30 arama,124 yük isteği ve159 kısa-soak isteği. Bunlar ayrı ölçüm birimleri. Hata incelemesi için accounts ve shared tanı tekrarları ana sayımı değiştirmedi; ilk başarısız sonuçlar korundu.

UI/perf ilk keşif betiğinde kilitli Nebim işlemlerine 501 ve2.1MB alan validasyonuna400 gelmesi hatalı beklentiyle FAIL işaretlenmişti. Bunlar uygulama kusuru sayılmadı: capabilities sözleşmesi501 ve alan doğrulaması400 doğru. Güncel targeted/negative kontrollere beklenen statüler açık yazıldı; global boyut testi9MiB Expect:100-continue ile413 doğruluyor. İlk keşif kanıtı ui-performance-results.json içinde aynen durur. Büyük gövdeyi bütünüyle yükleyen bir tanı tekrarında ECONNRESET oluştu (client-upload-reset.json); bu koşu başarılı gösterilmedi ve istemci mevcut güvenlik testindeki header-first yöntemine uyarlandı.

## Ana tur

{table(['Çalıştırma','Sonuç','Saniye','Ham çıktı'],suite_rows)}

## Test türü kapsamı

{table(['Tür','Durum','Gerçek kapsam / sınır'],testtypes)}

## Yeniden çalıştırma

Offline restore için tests/qa/empty-feed klasörünü oluştur, OfflineHook, GuardProbe, Probes projelerini bu kaynakla NuGetAudit=false restore/build et. Mevcut web/test obj/assets hazır olmalı; hazırlık yoksa internete otomatik çıkma. Sonra:

```powershell
node tests/qa/run-audit.cjs
node tests/qa/run-probes.cjs
node tests/qa/run-extended.cjs --targeted
python tests/qa/static-audit.py
python tests/qa/inventory.py
```

Probes performans çıktısını hazırladıktan sonra tam extended koşusu UI/yük/soak yapar. `--targeted` bu yükü tekrar etmeden 21 API ve6 düğme/modal kontrolü çalıştırır. run-probes ve targeted kusurları gördüğünde exit1 verir. Ana runner ilk başarısız pakette diğerlerini atlamaz. Ham makine sonuçları: {evidence('test-results.json')}, {evidence('targeted-results.json')}, {evidence('ui-performance-results.json')}, {evidence('invariants-probe.json')}, {evidence('backup-probe.json')}.

## Çalıştırılmayanlar

Canlı altı kanal/Nebim/WMS/taşıyıcı/ödeme, gerçek DB/transaction/migration/failover; Firefox/WebKit/Edge/Safari; uzun soak, çok süreçli ölçek, kuyruk saturasyonu; güncel dış advisory/SAST/DAST; Docker image ve gerçek TLS dağıtımı. Bunlar mevcut paketlerde skip edilmiş test sayısı olarak **0** toplamına gizlenmedi; ayrı **Doğrulanmadı** kapsamıdır. Otomatik ölçülmüş satır/dal kapsamı veya ürün tamamlanma yüzdesi yok.
''')
write('REGRESSION_REPORT.md',f'''# Regresyon raporu

{stamp}

**Yeşil değil: 61 mevcut test paketinde56 PASS,5 FAIL.** Build ve dış bağlantı engeli ayrıca PASS. Başarısızlar silinmedi, orijinalleri değiştirilmedi; güvenlik veya sahiplik kuralı gevşetilmedi.

{table(['Paket','Gerçek sonuç / sınıflandırma','İzleme'],[
 ('accounts-http-checks.cjs','Viewer credential-profiles403; test200 ve koşulsuz JSON bekliyor. Yeni rol korumasına eski test uyumsuz. accounts-diagnostic.log ile istek kesinleştirildi.','MP-QA-007'),
 ('catalog-http-checks.cjs','inventory ekranında eski katalog tablo seçicisi timeout.','MP-QA-008'),
 ('clothing-catalog-http-checks.cjs','WMS labels ekranında eski workflow filtre seçicisi timeout.','MP-QA-009'),
 ('operational-preparation-http-checks.cjs','İlk yerel sipariş400; yetkili fiyat bilinmiyor. Beklenti200 ve sonraki eski fiziksel stok düşümü kuralı güncellenmeli.','MP-QA-010'),
 ('shared-workspace-http-checks.cjs','Satır85 modül döngüsünde wuState().result timeout. Dashboard satır83’ü geçti; bu dashboard yükleme sorunu diye raporlanmadı. WMS override sonrası eski workflow beklentisi. Tanı tekrarında da aynı satırda FAIL.','MP-QA-009')])}

Geçen testler arasında canonical kimlik/birim/sürüm, ERP katalog salt okunur korumalar, fiyat politikaları, tenant sınırları, ERP/WMS orkestrasyonu, webhook ve worker izolasyonu var. Kullanıcının önceki Hepsiburada paket kimliği ve katalog eşleştirme/Yoksay eksikleri için güncel Hepsiburada/Synchronization birim paketleri ve catalog-stock-actions-http-checks.cjs **PASS**: alias eşleme satışı doğru ERP ürününe bağladı, Yoksay iptal/onayı ve restart/replay stok tek düşümü tarayıcıda denendi. Bu, gerçek Hepsiburada paket payload’ının sağlayıcı tarafından onayı değildir.

Yeni testler mevcut paketin göremediği MP-QA-001/002/003/004/005’i açığa çıkardı. Otomatik testler mevcut olsa bile iş kurallarının bütün veri yollarında doğru olduğu sonucu çıkarılamaz. Özellikle CanonicalStore atomik rezervasyon testi geçerken FulfillmentOrchestration aynı kapasiteyi iki siparişe tahsis edebiliyor.

Kapanış sırası: P1 kapasite ve yedek → backend null/DOM düğme → rol/ekran/fiyat fixture regresyon uyarlaması → modal ve CSP. Her düzeltmede ilgili negatif probe geçmeli, ardından mevcut63 tur yeniden koşulmalı; bu denetimde uygulama düzeltmesi yapılmadı.

Kanıt: {evidence('test-results.json')}; her FAIL için aynı adlı .log; [BUG_REGISTER](BUG_REGISTER.md).
''')

api_rows=[(e['name'],e.get('method','—'),e['status'],'PASS' if e['passed'] else 'FAIL',e.get('response','')[:130]) for e in target['api']]
endpoints=[(e['method'],e['path'] or '(grup kökü)',source(e['file'],e['line'])) for e in inventory['endpoints']]
write('BACKEND_API_REPORT.md',f'''# Backend ve API raporu

{stamp}

Web build:0 hata/0 uyarı. Mevcut23 .NET paketinin tamamı geçti. Yeni21 API kontrolünde **20 PASS/1 FAIL**; tek beklenmeyen500 CredentialReference eksik System (MP-QA-003). Sonraki health200; süreç çökmedi. Gerçek bağlantılar yapılmadı.

## Katmanlar ve davranış

- LocalAccess önce kimlik/oturum/Origin/Host kurallarını uyguluyor; TenantBoundary kaynak/hedef tenant alanlarını recursive denetliyor; BusinessWorkspaces cookie işletmesine göre backend servislerini ve veri kökünü seçiyor.
- Rollerde genel middleware ve modül endpoint filter’ları birlikte var. Admin mutation; read-only/warehouse/operations için deny/allow matrisleri mevcut. Sadece frontend filtrelemesi değil.
- Body boyutu8MiB, header sayısı64/toplam32KiB, bağlantı256; tenant JSON depth32. Beklenen domain hataları400/403/404/409/501/503; global beklenmeyen hatalar maskeli500.
- Ürün POST/DELETE/PATCH405, mevcut ürün PUT/barcode409; eski endpointlerin kalması koruma amaçlı. ERP preview/apply admin; eski ürün oluşturma butonu kaldırılmış.
- Filtre/sayfa query modelleri page/size/sort/tarih/aralık doğruluyor. Örneğin ListingQuery1–100 size; iç servisler bazı tam snapshot ihtiyaçlarında int.MaxValue kullanıyor, bu kamu API’si limit desteği olarak genellenmemeli.
- StockOutbox, EnterpriseSync ve inbox farklı kalıcı iş akışları. HTTP202 hedefte işlendi demek değil; durum/makbuz sorgusu ayrılıyor. Yeniden başlatma/timeout/hata testleri mock ile geçti.
- response error biçimleri tek standarda sahip değil (string/nested object/boş403). accounts regresyonundaki parser kırılması bu tutarsızlığın test bakımına etkisini gösteriyor; API contract sürümleme önerilir.

## Yeni negatif kontroller

{table(['Kontrol','Yöntem','HTTP','Sonuç','Cevap özeti'],api_rows)}

Nebim orders/events için501 hatalı başarı değildir: mock yazma capability varsayılan kapalı. İstek doğrulaması yazma izninden önce/sonra değişebilir; bu iki test yalnız desteklenmeyen işlemin sahte200 dönmediğini doğrular.

Tüm endpoint × rol × alan kombinasyonları test edilmedi. Tamper, tenant, yetkisiz, malformed, dev boyut, kaynak sahipliği ve servis kesintisi örnekleri mevcut paketlerde test edildi; aşağıdaki her route için bu kapsam otomatik ispatlanmış sayılmaz. Özellikle bütün metadata lookup’ları, dinamik Trendyol işlem kataloğunun her operasyonu, tüm finans mutasyonları ve tüm nested null kombinasyonları ayrı backlog’dur.

## Endpoint bildirim envanteri

Grup/prefix birleşimleri inventory.json groups içinde. Aşağıdaki path’ler kaynakta yazıldığı haliyle; gruba göreli path’ler tam URL değildir. Aynı deklarasyon farklı kanal/hesap/store parametreleriyle genişler. UI çağrısı görülmeyen endpointler otomatik “kullanılmıyor” ilan edilmedi: webhook, provider ve worker/dış tüketiciler mevcut.

{table(['Yöntem','Bildirilen path','Dosya / satır'],endpoints)}
''')

screen_areas={
 'products':'ERP katalog: salt okunur + preview/apply', 'mapping':'Catalog mapping + aday arama', 'categories':'Giyim kategori binding', 'inventory':'WMS inventory / policy / history', 'pricing':'ERP fiyat policy / preview / approval (mock)', 'wms-fulfillment':'WMS workflow / policy (mock)', 'shipping':'WMS lojistik', 'tracking':'WMS lojistik', 'labels':'WMS etiket yetkisi', 'returns':'WMS iade/kalite onayı', 'dashboard':'overview/reports;3 düğme hatalı', 'connections':'IntegrationRegistry / central-stock', 'sync':'EnterpriseSync / automation / outbox', 'barcodes':'ERP salt okunur', 'listings':'Catalog ilan/job hazırlığı', 'bulk':'Catalog seçili ilan hazırlığı', 'reports':'Analytics API', 'profit':'Analytics maliyet/fiyat API', 'alerts':'Analytics/queue bulguları', 'activity':'Audit/işlem kayıtları', 'automation':'Zamanlayıcı/policy', 'settings':'Kalıcı ayarlar', 'users':'LocalAccess / Tenant / yedek API' }
for c in ['trendyol','amazon','hepsiburada','n11','boyner','ticimax']:screen_areas[c]='Kanal native/demo backend; testlerde mock/fixture'
screenrows=[(r['id'],r['name'],screen_areas.get(r['id'],'Workspace/Operations API; yerel talep/taslak, native işlem değil'),'3 genişlik açıldı') for r in ui['navigation']]
write('FRONTEND_UI_REPORT.md',f'''# Frontend / UI raporu

{stamp}

**42 menü ekranı ×3 genişlik =126 açılış.**1440×1000,768×1024,390×844. Açılış taramasında0 uncaught pageerror,0 API500 ve0 document yatay taşma. Bu sonuç bütün düğmelerin çalıştığı anlamına gelmiyor: gerçek tıklamada3 dashboard düğmesi FAIL.30 JS dosyası parser kontrolü PASS olmasına rağmen HTML içinde oluşan inline handler bozuk (MP-QA-004).

Chrome156 kullanıldı. Firefox/WebKit binary yok; Edge/Safari ve gerçek mobil cihaz testi yapılmadı. Dış resim/font istekleri test katmanında engellendi; canlı görsel erişilebilirliği doğrulanmadı. Mock ERP yerel SVG’si mevcut erp-read-model testinde yüklendi.

## Ekran/backend envanteri

{table(['Ekran ID','Başlık','Bağlandığı katman / sınır','Gözlem'],screenrows)}

## Etkileşimler ve kullanıcıya gösterilen durum

Mevcut UI paketleri ürün oluşturma yasağı, katalog fark uygulama, kategori/form, filtre/sayfalama, altı kanal merkezi, fiyat onayı, alias katalog eşleme, Yoksay iptal/onayı, outbox, ERP hatası/timeout görünümü ve mock WMS senaryolarının önemli bölümlerini içeriyor. Başarısız5 paket içindeki hatadan sonraki adımlar çalıştırılmış sayılmadı. Görsel olarak bütün ekranın açılması her buton/mutasyon için E2E kapsama eşdeğer değil.

Yeni gerçek düğme testinde Satış Kanalları oku, Son Siparişler Tümünü gör ve Son İşlemler oku JavaScript SyntaxError verdi. Görsel durumu {evidence('targeted-results.json')} ve app.js:17/21 destekliyor. Genel router menü linkleri ile page.evaluate(nav) çalışıyor; bu üç button bozuk.

Gerçek parola modalında dialog rolü/aria-modal, ilk odak ve Escape kapatma yok;3 test FAIL. Masaüstü taramasında2 görünür adsız ikon düğmesi gözlendi (aynı dashboard okları). Tablet/mobilde bütün42 ekrandaki hamburger düğmesinin erişilebilir adı ve aria-expanded bilgisi yok (MP-QA-012). İçerik formlarında test edilen görünür input/select için etiketsiz kontrol tespit edilmedi; bu bir accessible-name algoritması veya WCAG sertifikası değil. Kontrast, zoom200%, Tab döngüsü ve ekran okuyucu backlog.

ERP/WMS sayfaları mock kaynağını ve onay/sorgu/blocked durumlarını açıklıyor. nativeAdapter/provider-contract-pending backend bilgisi var; live başarı iddiası yapılmıyor. Pricing boş input ve eski kaynak görünümü mesajlarında bozuk Türkçe var (MP-QA-006, ErpPricingService.cs:77/84). WMS lojistik policy details içinde ham JSON teknik sunum kullanıyor; son kullanıcı için okunabilir politika özeti ürün iyileştirmesidir.

Eski global placeholder ve render override’ları bakım riski; catalog/shared/clothing eski seçicileri güncel WMS ekranını yanlış modelle bekliyor. Öneri: tek modül router, event-listener tabanlı düğmeler, ortak erişilebilir dialog, kararlı işlevsel test seçicileri ve rol odaklı gezinme testleri.

Kanıtlar: {evidence('ui-performance-results.json')}, {evidence('targeted-results.json')}, {evidence('password-modal.png')}, {evidence('resilience/erp-duplicate-sku.png')}, {evidence('resilience/erp-timeout.png')}, {evidence('resilience/worker-error.png')}.
''')
write('ERP_WMS_REPORT.md',f'''# Nebim V3 ve WMS denetimi

{stamp}

**Her iki gerçek sağlayıcı da hazır değil.** Doğrulanmış Nebim transport/servis/SQL/yazma sözleşmesi ve seçilmiş WMS sözleşmesi yok. Gerçek bağlantı denenmedi; native endpoint/alan uydurulmadı. Uygulamadaki /api/erp/nebim route’ları MarketPilot uygulama route’larıdır, Nebim API URL’leri değildir.

## Ortak veri modeli

Contracts/CanonicalModels.cs:5–30 içinde17 model var: CanonicalProduct, ProductVariant, Barcode, ExternalProductMapping, Warehouse, StockPosition, StockReservation, AvailableToSell, PriceList, MarketplaceOrder, OrderLine, FulfillmentRequest, Shipment, ReturnRequest, InvoiceReference, IntegrationEvent ve SyncCheckpoint. RecordContext tenant/source/account/correlation/idempotency/source version/kaynak-alım zamanlarını taşıyor. WarehouseId gerekli depo modellerinde; OrderLine üst sipariş bağlamı içinde gömülü, ATS türetiliyor.

CanonicalChecks PASS: aynı SKU/farklı barkod (açık aynı varyant), farklı varyantta aynı barkod ve belirsiz SKU manuel kuyruk, çok depo, çok ilan/hesap, BOX↔EA ve CM↔M, decimal/whole units, eksik kaynak kimliği, değişmiş/eşit/eski sürüm, replay/checkpoint ve concurrency/rollback/restart. Ada göre otomatik kimlik yok. Bu katmanın başarılı atomik rezervasyonu ayrı fulfillment iş akışının kapasite garantisi değildir.

## Nebim

{table(['Alan','Durum / kanıt'],[
 ('Arayüz / profile / capabilities / result / error','INebimErpProvider, ErpConnectionProfile, ayrı read/write flags, ErpSyncResult ve ErpError mevcut. NebimEndpoints.cs:13 yalnız MockNebimProvider kaydı.'),
 ('Ürün ve varyant / barkod / SKU / fiyat / depo-stok / belge','Mock okumalar var. Sürüm ve son sync checkpoint takip ediliyor.'),
 ('Yazma / event / order status','AllowMockWrites ayrı; default kapalı501. Native mod NotSupported, sessiz200 değil.'),
 ('ERP salt okunur katalog','CatalogHub/Store/Source ve ErpCatalogReadModel ile preview/apply; marka/kategori/açıklama/onaylı görsel; mapping korunması ve missing-source review testleri PASS.'),
 ('Fiyat','Yetkili listeler, kanal eşlemesi, KDV tabanı, açık kurallar, komisyon/masraf, alt/üst limit, kampanya önceliği, approval/versiyon/expiry; unknown null, yanlış currency blocked. RealPriceWrites kapalı.'),
 ('Sipariş aktarım hazırlığı','ErpOrderTransferService: satış kanalı/cari/varyant/KDV/tutar/kampanya, source identity/idempotency, kabul referansı/sorgu, ayrı create/correct/cancel/return. Mock accepted/rejected/pending/timeout/duplicate ve mutabakat senaryoları PASS.'),
 ('Gerçek adaptör','Eksik / beklemede: Contracts.cs:17 açık TODO. Mevcut EnterpriseBridge özel MarketPilot bridge sözleşmesidir; native Nebim olarak isimlendirilmemeli.')])}

## WMS

Bağımsız Wms/IWmsProvider profile/capability/warehouse/stock/movement/reservation/fulfillment/shipment modelleri mevcut. Mock deterministik veri, gecikme/hata, kabul/rezervasyon/toplama/paket/sevk/uygunluk sorguları; native provider kapalı. Enterprise namespace altındaki eski IWmsProvider ayrı özel bridge okuma sözleşmesi olarak yaşamaya devam ediyor.

WmsInventoryService depo bakiyelerini ayrı tutuyor. Fiziksel/rezervasyon/hasarlı/bloke/iade kontrol/ayrılmış/transfer ve kaynak versiyon/zamanı; işletme onaylı deduction policy, warehouse-account atama, güvenlik stoğu/kanal limitleri; FBA/n11depom ve unknown/negatif koruması; eski/çelişkili kaynak incelemesi testleri PASS. Ancak kaynak yaşına göre stok yayınını otomatik kapatan açık işletme tazelik SLA’sı ve tüm kanallara uygulanan tek ortak rezervasyon otoritesi henüz üretim seviyesi değil.

FulfillmentWorkflowPolicy ERP/WMS sırasını belirliyor; belirsiz işlem blocked/uncertain, external request ID/idempotency, kabul≠işlendi, kısmi sevk/çok paket/çok depo, depo reddi ve timeout sorgulama var. **MP-QA-001: satılabilir30 için iki bağımsız sipariş60 tahsis alıyor ve iki WMS kabulü geliyor.** Bu yüzden stok/fulfillment tamamlandı denemez. Test gerçek iki siparişin paralel alınması ve provider kabulüyle tekrarlandı.

WmsLogistics olayları sevkiyat/paket/carrier/tracking/toplama/paketleme/sevk zamanı/kısmi sevk/başarısız teslimat/iade kabul/kalite/yeniden satış bilgisini temsil ediyor. Kanal durum eşlemesi ve WMS/carrier/marketplace etiket yetkisi ayrı. Etiket dosyası uydurulmuyor; WMS restock onayı olmadan satılabilir iade işaretlenmiyor, fiziksel stok doğrudan artırılmıyor. Mock UI/API durum geçişleri PASS; gerçek carrier ve kanal teyidi yok.

## Merkezi orkestrasyon

12 event adı tanımlı: ProductChanged, PriceChanged, StockChanged, MarketplaceOrderReceived, ErpOrderAccepted, WmsOrderAccepted, StockReserved, PickCompleted, PackageCompleted, ShipmentDispatched, OrderCancelled, ReturnAccepted. Tenant/hesap/EventId/CorrelationId/source version/source-target/zaman/sonuç/hata mevcut. Poller ve webhook başarı/checkpoint öncesi EnterpriseSync içine kaydediyor. Retry belirsizde sorgu yoluna gidiyor; duplicate/stale/eşit versiyon conflict ve manuel müdahale testleri var.

Event adının bulunması her hedef kombinasyonunun işlendiği anlamına gelmez. IntegrationOrchestrator.ProcessAsync desteklenen event×target dallarını yürütüyor, doğrulanmamış kombinasyonu bloke ediyor. ERP/WMS erişilemezliğinde mock sipariş/iş kaybı test edilmediği söylenemez: orchestrator HTTP restart/recovery geçti; ancak gerçek dağıtık DB/outbox atomikliği ve bütün kesinti noktaları doğrulanmadı.

## Açık işler ve çıkış kriteri

1. MP-QA-001 ve MP-QA-002 kapanmalı; siparişler/depolar arası ortak kapasite, restart ve iade/iptalde doğru serbest bırakma testi.
2. Yetkili Nebim ve WMS teknik sözleşmesi, anonymized gerçek fixture ve her yetenek için doğrulanmış mapper.
3. Provider integration’ın credential-reference yaşam döngüsü, accepted/processed/receipt, rate limit/date/cursor/version semantiği onaylanmalı.
4. Tek stok otoritesi, freshness/uzlaştırma, kanal yayınından önce policy/onay ve bağımlı süreçlerin veri akışı birleştirilmeli.
5. Native belge referansı/iptal/iade/düzeltme ve mutabakat muhasebe uzmanıyla doğrulanmalı; üretim yazmaları bu fazda kapalı kalmalı.

Kanıt: NebimChecks, ErpReadModelChecks, PricingChecks, WmsChecks, WmsLogisticsChecks, ErpTransferChecks, OrchestratorChecks logları ve ilgili HTTP logları; {evidence('invariants-probe.json')} / {evidence('backup-probe.json')}.
''')

channels=['Trendyol','Hepsiburada','n11','Amazon TR','Boyner','Ticimax']
matrix=[
 ('Bağlantı / kimlik','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('Ürün okuma / SKU-barkod / ilan mapper','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('Gerçek ürün oluşturma','Engelli','Engelli','Engelli','Engelli','Engelli','Engelli'),
 ('Stok outbox / yazma hazırlığı','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('ERP fiyat policy / mock yayın','Demo','Demo','Demo','Demo','Demo','Demo'),
 ('Gerçek yeni fiyat aktarımı','Kapalı','Kapalı','Kapalı','Kapalı','Kapalı','Kapalı'),
 ('Sipariş çekme','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('Paket / native durum okuma','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('WMS→kanal sevk / durum yazma','Kısmi / Demo','Kısmi / Demo','Kısmi / Demo','Kısmi / Demo','Kısmi / Demo','Kısmi / Demo'),
 ('İptal / iade read ve talep hazırlığı','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('Doğrulanmış uçtan uca native refund','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı'),
 ('Webhook alımı / inbox','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('Rate limit / hata / retry','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('Pagination / cursor / tarih mapper','Tamamlandı*','Tamamlandı*','Tamamlandı*','Tamamlandı*','Tamamlandı*','Tamamlandı*'),
 ('Async receipt / idempotency','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi','Kısmi'),
 ('Kanal yönetimli stok','Merchant sınırı','Merchant sınırı','n11depom koruması','FBA koruması','Merchant sınırı','Merchant sınırı'),
 ('Mevcut native .NET fixture paketi','PASS','PASS','PASS','PASS','PASS','PASS'),
 ('Mevcut kanal HTTP/UI paketi','PASS','PASS','PASS','PASS','PASS','PASS'),
 ('Gerçek hesap / native API teyidi','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı','Doğrulanmadı')]
write('MARKETPLACE_REPORT.md',f'''# Altı pazaryeri raporu

{stamp}

**Altı kanalın tamamı canlıya çıkış için NO-GO; mock/fixture testleri geçti.** Native okuma/yazma kodunun bulunması üretim hesabında doğrulandığı anlamına gelmez. Ortak mapper için ChannelContractChecks çıktısı239 kontrol PASS bildiriyor; bu yalnız o paketin kendi sayacı, bütün projenin test sayısı değil.

{legend}

{table(['İşlem']+channels,matrix)}

*Tamamlandı yalnız mevcut mapper/fixture kapsamındaki filtre taşıma ve desteklenmeyen filtre reddi anlamında. Her native sunucunun güncel tarihte bütün alanları aynı semantikle desteklediği doğrulanmadı. Engelli/Kapalı hatalı eksik değil; ürün ana kaynağı ve gerçek fiyat gönderimi sınırına uygun güvenlik kararı.

## Kanal farkları

- **Trendyol:** product/content/main ID+SKU+barkod; packageId. Ürün nextPageToken/order nextCursor ile bounded tarih taraması. Birçok ayrı native panel ve operation catalog mevcut (ürün, sipariş, iade, fatura, etiket, finans, webhook vb.); bu gelişmiş araçlar merkezi ERP/WMS workflow ile aynı kapsamda değildir. createV1/createV2 endpointte405; eski metadata tanımı native ürün oluşturma yetkisi sayılmaz. Batch kabulü receipt ile ayrılıyor. IntegrationChecks, trendyol-workflows ve ChannelContractChecks PASS.
- **Hepsiburada:** listingId/HB SKU, merchantSku ve barkod ayrı; order-line kimliği paket numarasından ayrılmalı. Güncel adapterde paket taraması var; fixture tests repeat/order-package identity ve çift satış düşmeme kontrollerini geçti. Paket sayfalaması≤10, header toplamı,24saat paket ve31gün sipariş pencereleri kodun mevcut sözleşmesi. Bunlar bu denetimde sağlayıcıdan yeniden onaylanmadı. Gerçek anonim paket yanıtıyla nihai kimlik teyidi canlı faz kapısı olmaya devam ediyor.
- **n11:** product ID/seller SKU, bazı barkod filtreleri desteklenmiyor ve açık reddediliyor. n11depom stock provider-managed; task kabulü uygulanma değil. Merchant stok yazımı öncesi sender doğrulanıyor. İade/finans okuması ve tam native mutation desteği birbirine eşit kabul edilmedi.
- **Amazon TR:** ASIN/content ve seller SKU; barkod bulunmayabilir. nextToken ve tarih gecikme kısıtları; native paketler korunuyor. FBA/AMAZON/AFN stok yazımı engelli; çok konumlu merchant stokta supply-source eşlemesi olmadan yazılmıyor. Eksik VAT/gross/discount/currency tahmin edilmiyor. SP-API/LWA ve native fixture kodu var; gerçek token/izin/veri erişimi denenmedi.
- **Boyner:** supplier/product/content ID+SKU+barkod; page/date sınırları, nullable KDV ve native alanların korunması. Yazma kodunda mağaza rate aralığı/gate ve batch receipt kontrolü var. Stok isteğinin mevcut fiyat alanlarıyla birlikte native gönderilmesi kapsamı ve güncel vendor sözleşmesi canlı öncesi ayrıca onaylanmalı.
- **Ticimax:** tenant’a özel HTTPS SOAP hedefi, WSDL metot/response parsing ve XML güvenliği; kart/varyant ID+SKU+barkod. Para birimi kaynaktan gelmezse uydurulmuyor; orderNumber için sahte paket ID yok. page:N token, native durum kodu ve tarih filtreleri var. Native metot/yetki sürümü müşteri kurulumuyla doğrulanmalı; demo kargo/iade/finans ekranı native mutation desteği değildir.

## Ortak riskler

Webhook route/inbox ve sağlayıcı imza politikalarının uygulamada bulunması her kanalın native webhook protokolüyle onaylandığı anlamına gelmiyor. API tarafından desteklenmeyen işlem501/409 veya review durumuna gidiyor; bütün kanallarda aynı durum adı varsayılmıyor. 429/5xx/timeout/retry testleri fixture düzeyinde; sağlayıcı kotası/production throttling fiilen ölçülmedi. Asenkron kabul, job reference ve işlenmişlik durumu alanları ayrılmıştır; gerçek receipt süreleri yok.

Account/tenant/credential profile ayrımı backend testlerinde doğrulandı. accounts HTTP paketinin FAIL’i viewer credential metadata erişimi beklemesinden; güvenli403 doğru, test yanlış. StockOutbox gerçek yazmaları açıkça konfigürasyona bağlı; gerçek fiyat ise tüm kanallarda hâlâ kapalı. Test sırasında teknik egress engeli + sahte kimlik + loopback HTTP fixture kullanıldı.

Canlı fazda her kanal için sıra: salt okunur sandbox/account doğrulama → anonim payload contract → cursor/date/retry/permission matrisi → az ürünle guardrail ve receipt doğrulama → stok sevk/iptal/iade mutabakatı → kontrollü yazma onayı. Bu denetim bu adımların hiçbirini canlıda yapmadı.

Kanıt: ChannelContracts.cs:31–81 ve kanal mapper; ilgili adaptör/endpoint kaynakları; AccountWriteChecks / ChannelContractChecks / altı kanal .NET logları; amazon/hepsiburada/n11/boyner/ticimax HTTP logları ve trendyol-workflows; [ana test tablosu](ALL_TEST_RESULTS.md).
''')

write('SECURITY_REPORT.md',f'''# Güvenlik raporu

{stamp}

Test edilen sınırlar içinde yeni tenantlar arası veri sızıntısı, yetki yükseltme veya sömürülebilir XSS kanıtı bulunmadı. Bu bir “güvenlik açığı yoktur” sertifikası değil. **P1 yedek eksikliği erişilebilirlik/veri kurtarma riski; P2 CredentialReference null500 ve P2 inline handler CSP riski mevcut.**

{table(['Kontrol','Durum','Kanıt / pratik sınır'],[
 ('Parola / oturum','PASS (yerel)','PBKDF2-SHA256210k, random salt, sabit zaman kıyas; yanlış parola401/doğru200; version invalidate;8saat sliding olmayan cookie. MFA/SSO ve harici session store yok.'),
 ('Cookie','PASS','HttpOnly, SameSiteStrict; public mod SecureAlways. Local loopbackHTTP Secure olmayabilir; bu üretimHTTPS ayarıyla karıştırılmadı.'),
 ('RBAC / tenant','PASS (test edilen)','Altı rol, cookie membership kontrolü, source/target header/query/body reddi; tenant’a ayrı servis/JSON kökü. Yatay/dikey saldırılar, export/rapor/webhook/queue/audit/bağlantı sınırları mevcut testlerde geçti.'),
 ('CSRF / Origin / CORS','PASS (test edilen)','Foreign Origin403, diğer site okuma/yazma sınırları; SameSite cookie/host kontrolü. Mevcut oturum düzeni değiştirilmedi; ayrı token modeli zorunlu kılınıp koruma kapatılmadı.'),
 ('CSP / XSS','Kısmi','Escaping/jsArg ve modern script-src-elem self; inline attr unsafe-inline var (MP-QA-011). Mevcut saldırı fixture testleri PASS, bütün DOM sink kombinasyonları test edilmedi.'),
 ('HTTP header / HTTPS','PASS (uygulama simülasyonu)','nosniff, DENY/frame-ancestors, referrer no-referrer, COOP/CORP, permissions ve no-store. Public TLS/proxy header testleri PASS; gerçek sertifika/Caddy TLS handshake denenmedi.'),
 ('SSRF / URL / DNS','PASS (fixture)','OutboundTargets private/mapped/metadata/redirect denetimi, açık onaylı private host, DNS-pin; Ticimax HTTPS/allowlist. Gerçek DNS altyapısı ve OS firewall pentesti değil.'),
 ('CredentialProfiles / CredentialReference','Kısmi','Secret değeri UI/JSON connection kaydına yazılmıyor. Env/key-per-file referansı; JSON secret config startup’ta reddediliyor. {} referansı500 (MP-QA-003); mock alanları için pozitifler geçti.'),
 ('Webhook','PASS (mock)','İmza/timestamp/idempotency/secret rotasyonu ve tenant inbox, retry/duplicate/payload sınırı. Her native sağlayıcının protokolü canlı onaylanmadı.'),
 ('Log / PII / CSV','PASS (fixture)','Authorization/config secret/email/telefon masking, genel sanitized500, CSV formül/locale koruması. Tüm olası kişisel veri desenlerinin anonimliği ispatlanmadı.'),
 ('Dosya / path / backup','Kısmi','Private Data/keys/webroot dışı dosya erişimi ve path traversal red; şifreli/hash’li yedek tamper testi PASS; kapsam kaybı MP-QA-002.'),
 ('Request/resource limits','PASS (örnekler)','8MiB body, header64/32KiB, JSONdepth32, login throttling; header-first9MiB413, ardından health200. Genel DDoS dayanıklılığı değil.'),
 ('Üretimde gerçek bağlantı','PASS (guard)','IntegrationSecurity.AllowRealConnections defaultfalse public mod; mock writes ayrı; gerçek fiyat kapalı. Operator ortamında sonradan açılacak ayarlar ayrı rollout gerektirir.'),
 ('Dependency CVE','Doğrulanmadı / cache','npm audit --offline0 bildiriyor; current advisory registry sorgulanmadı. .NET NuGet package reference0; .NET8 runtime ve container/base image CVE/SBOM güncel taraması yapılmadı.')])}

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

Kanıtlar: {evidence('MarketPilot.SecurityChecks.log')}, {evidence('security-hardening-http-checks.cjs.log')}, {evidence('tenant-security-http-checks.cjs.log')}, {evidence('tenant-isolation-audit.log')}, {evidence('webhook-http-checks.cjs.log')}, {evidence('npm-offline-audit.log')}.
''')

perfrows=[]
for m in performance['metrics']:perfrows.append((m['size'],f"{m['writeMs']:.2f}",f"{m['p50Ms']:.2f}",f"{m['p95Ms']:.2f}",f"{m['workingSetBytes']/1024/1024:.1f}",f"{m['privateBytes']/1024/1024:.1f}",f"{m['cpuMs']:.2f}",m['fileBytes']))
loadrows=[]
for m in ui['performance']:loadrows.append((m['concurrency'],m['count'],str(m['statuses']),f"{m['p50Ms']:.2f}",f"{m['p95Ms']:.2f}",f"{m['maxMs']:.2f}",f"{m['after']['workingSet']/1024/1024:.1f}",f"{m['cpuSeconds']:.3f}"))
write('PERFORMANCE_REPORT.md',f'''# Kontrollü performans raporu

{stamp}

Tamamen sentetik ürünler; kullanıcı demo deposu değil, sistem temp altında ayrı MarketPilot kökü. Gerçek sağlayıcı ve DB yok. Sınır512MiB test-process working set ve p95>2sn durdurma koşulu. Yük az tutuldu; diğer süreçler durdurulmadı veya ayarları değiştirilmedi.

## 100 / 1.000 / 10.000 kayıt

Her boyutta mevcut küçük demo seed’ine N adet QA-SKU eklendi; dosya yazımı ve30 sayfalı arama ölçüldü. Her aramada sentetik toplam=N ve25 sonuç doğrulandı. Süreler tek lokal çalıştırma, ısınma/GC etkileri dahil; benchmark güven aralığı yok.

{table(['Sentetik N','Yazma ms','Arama p50 ms','p95 ms','Working set MiB','Private MiB','CPU ms','JSON bytes'],perfrows)}

Arama filtre/sayfalama işlemi DemoStore üzerinde yapıldı; ERP veya pazaryeri roundtrip süresi değil. Tekil snapshot yazımı toplu10k ekleme;10k ayrı update performansı iddia edilmedi.

## HTTP yük / kontrollü kısa stress

10.000 sentetik kayıt, GET /api/products/search?search=QA%20synthetic&size=25; loopback istemci. Her yanıt tamamen okundu, sonuç bütünlüğü önceki katmanda test edildi. Eşzamanlılık aşamalı1→4→8; toplam124 HTTP/0 hata.

{table(['Eşzamanlı','İstek','HTTP','p50 ms','p95 ms','Max ms','Son RAM MiB','CPU fark sn'],loadrows)}

Bu seviyede hata yok; “maksimum kapasite8” veya daha fazla kaldıramaz sonucu çıkarılmadı. Süreç CPU farkı Get-Process ile, RAM process working/private bytes ile ölçüldü; makinenin toplam CPU yüzdesi değil. Toplam batch elapsed ölçümü örnekleme komutu maliyetini de içerdiğinden kesin throughput kapasitesi raporlanmadı.

## Kısa soak

{ui['soak']['durationMs']/1000:.1f} saniye, {ui['soak']['count']} istek, {ui['soak']['failures']} hata;150ms istemci beklemesi. Örneklenen en yüksek working set {max(r['workingSet'] for r in ui['soak']['samples'])/1024/1024:.1f}MiB. Bellek yükselip GC sonrası geriliyor; tek30sn gözlemle leak var/yok denemez.512MiB sınırına gelinmedi.10k JSON’un tam snapshot modeli kısa sürede yüzlerce MiB’ye ulaştığından bu paylaşılan makinede daha büyük veri/yük ve uzun soak yapılmadı.100k/1m kayıt, saatler boyu test, çoklu tenant iş hacmi ve kuyruk saturasyonu **Doğrulanmadı**.

## Mimari kapasite riskleri

DemoStore filtrelemesi/serileştirmesi tam veri hacmiyle büyüyor; JSON’u clone/serialize etmek ve global lock yazmaları aynı süreçte kuyruklar. Çok dosyalı transaction ve process’ler arası row locking yok. Raporların/operations overview’un büyük order/queue setindeki davranışı bu ürün arama yüküyle ölçülmedi. Worker kapasite/retention ve dead-letter artışı için kontrollü ayrı benchmark gerekli. Native quota/timeouts ve DB indeksleri için şu an ölçüm yok.

Kanıt: {evidence('performance-probe.json')}, {evidence('ui-performance-results.json')}; test kodu Probes/Program.cs ve extended-checks.cjs. Bu sonuçlar yalnız yerel sentetik faza ait; canlı SLA veya üretim ölçek onayı değil.
''')

bugsections=[]
for b in bugs:
 id,priority,kind,title,file,line,log,repro,expected,actual,suggestion,regression=b
 bugsections.append(f'''## {id} · {priority} · {title}

- Tür / durum: **{kind} — açık**. Bu denetimde düzeltilmedi.
- Kaynak: {source(file,line)}.
- Kanıt: {evidence(log)}.
- Tekrar üretme: {repro}
- Beklenen: {expected}
- Gerçek: {actual}
- Çözüm önerisi: {suggestion}
- Gerekli regresyon: {regression}
''')
write('BUG_REGISTER.md',f'''# Hata ve risk kaydı

{stamp}

**11 doğrulanmış kusur grubu:**7 uygulama ve4 test altyapısı. Ayrıca1 CSP güvenlik riski kaydı. **P0=0, P1=2, P2=8 (risk dahil), P3=2.** Tek kök neden birden fazla test/düğme hatasını kapsayabilir. “Risk” kaydı sömürülebilir saldırı kanıtı gibi sunulmadı.

Öncelik: P0 kritik kayıp/ihlâl; P1 stok/veri kurtarma gibi yüksek iş etkisi; P2 işlev, güvenlik katmanı veya regresyon güvenilirliği; P3 kullanıcı mesajı. Native sözleşme/gerçek DB gibi ertelenmiş işler hata sayımına eklenmedi; [MISSING_FEATURES](MISSING_FEATURES.md) ve [BACKLOG](PRIORITIZED_BACKLOG.md) içinde.

{table(['ID','Öncelik','Tür','Bulgu'],[(b[0],b[1],b[2],b[3]) for b in bugs])}

'''+ '\n'.join(bugsections))

missing=[
 ('M-01','Eksik / kasıtlı bekliyor','Native Nebim adaptörü ve doğrulanmış transport/yazma/sorgu/error sözleşmesi','Yetkili teknik belge, örnek ve test kurulumu; hayali endpoint yok.'),
 ('M-02','Eksik / kasıtlı bekliyor','Seçilmiş native WMS sağlayıcı adaptörü','Depo/capability, rezervasyon ve receipt/freshness sözleşmesi.'),
 ('M-03','Kısmi / kusurlu','Tek otoriteyle uçtan uca atomik stok tahsisi','MP-QA-001, aynı fiziksel bakiye için canonical/operational/workflow rezervasyon ilişkisinin tekilleşmesi.'),
 ('M-04','Hatalı','Bütün modülleri kapsayan sürümlü yedek/restore','MP-QA-002; arşivin şifreli olması yeterli değil.'),
 ('M-05','Doğrulanmadı','Altı sağlayıcıda gerçek auth/izin/payload/receipt ve native yazma','Sandbox/anonim payload ve sağlayıcı onayı; özellikle HB paket/kalem kimliği ve Ticimax WSDL sürümü.'),
 ('M-06','Kısmi / kasıtlı kapalı','Gerçek ERP fiyat yayınlama','Politika hesabı mevcut; gerçek gönderim bu fazda açılmamalı. Onaylı native transport ayrıca geliştirilip test edilmeli.'),
 ('M-07','Kısmi','Her olay×hedef için açık capability/mapper ve tamamlanmış durum yayınlama','12 olay adı var, bütün kombinasyonlar yazmaya uygun değil; mevcut safe-block doğru.'),
 ('M-08','Kısmi','Stok tazelik SLA ve tüm kaynaklar arası otomatik olmayan uzlaştırma yönetimi','Onay/TTL/çatışma inceleme, source checkpoint lag, stok yayını durdurma ve kurtarma prosedürü.'),
 ('M-09','Kısmi','Native e-fatura/e-arşiv/ödeme/geri ödeme/taşıyıcı etiketi','Yerel taslak/referans/demo ile gerçek mali belge/para hareketi ayrılmalı. Uydurma belge yok.'),
 ('M-10','Eksik / ertelenmiş','DB şeması, migration, unique constraint, transaction/outbox ve backup/PITR','Bu denetimde gerçek DB kurulmadı; tasarım ve izole DB test fazı ayrıca yetkilendirilmeli.'),
 ('M-11','Eksik','Çok süreçli HA/worker lease/rolling upgrade','Şimdiki JSON lock tek süreç garantisi; dağıtık yürütücü eklemeden mevcut kuyruğun DB tasarımı.'),
 ('M-12','Kısmi','Tam gözlemlenebilirlik / alarm / dead-letter operasyonu','Local trace/audit var; merkezi metrik, on-call alarm, SLO, retention ve DR runbook testi tamamlanmadı.'),
 ('M-13','Hatalı / kısmi','Güvenilir tam regresyon ve bütün API×rol matrisi','5 eski paket fail; hata sonrası adımlar kapsam dışı. Yeni negatiflerde gerçek kusurlar var; line/branch coverage ölçümü yok.'),
 ('M-14','Kısmi','Tarayıcı/erişilebilirlik','Chrome var; Firefox/WebKit/Edge/Safari, screenreader, kontrast, keyboard kapsamı eksik; modal bug.'),
 ('M-15','Doğrulanmadı','Güncel dependency/SAST/DAST/container güvenlik kapıları','Offline cache0 açık sonucu güncel CVE güvencesi değil; SBOM/advisory freshness gerek.'),
 ('M-16','Doğrulanmadı','Üretim benzeri yük/uzun soak/queue kapasitesi','10k arama/8 istemci/30sn küçük probe var; çok tenant ve gerçek quotas yok.'),
 ('M-17','Kısmi','CI artifact/test raporu ve deploy/rollback/TLS tatbikatı','CI YAML var, local Compose config geçti; Linux runner, image, TLS ve restore rollback çalıştırılmadı.'),
 ('M-18','Kısmi','Credential yaşam döngüsü ve privacy/retention politikası','Env/key-per-file var; sağlayıcı native bağlama, rotasyon/iptal, scoped metadata ve PII retention işletme onayına ihtiyaç duyar.')]
write('MISSING_FEATURES.md',f'''# Eksik ve doğrulanmamış özellikler

{stamp}

Eksik, bilerek kapalı ve doğrulanmadı birbirinden ayrıldı. Gerçek DB/Nebim/WMS bağlantısının olmaması kullanıcı sınırına uygun; bu fazda “açılması gereken hata” değil. Yerel mock fazda kapatılması gereken gerçek hatalar ayrıca belirtiliyor.

{table(['ID','Durum','İş','Gereken kanıt / tamamlanma ölçütü'],missing)}

Ada göre ürün eşleştirme, uydurma para birimi/fiyat, WMS onaysız iade stoğu, kör belirsiz yazma tekrarı veya hayali Nebim endpoint’i bu eksikleri tamamlamanın yolu değildir. Kaynak belirsizse inceleme kuyruğu ve açık capability sınırı korunmalı.
''')

write('PRIORITIZED_BACKLOG.md',f'''# Öncelikli backlog ve gün planı

{stamp}

Bu denetim kod düzeltmedi. Aşağıdaki süreler tek geliştirici için yaklaşık iş günleridir; teknik sözleşme/sağlayıcı cevabı için bekleme süresi hariç. Her günün çıkışı somut test kanıtı olmalı. Yerel faz boyunca gerçek bağlantı ve DB kapalı kalır.

{table(['Sıra / gün','Öncelik','İş','Çıkış ölçütü'],[
 ('1–2','P1','MP-QA-001 ortak tahsis otoritesi; WMS acceptance/processed/reconciliation ayrımı','30 stok için iki30 siparişin toplam etkin tahsisi≤30; sıra/paralel/restart/timeout/iptal/partial testleri.'),
 ('3','P1','MP-QA-002 modül kayıtlı sürümlü yedek/restore','Altı eksik modül sentinel’i ayrı restore klasöründe aynı; eksik dosyada verify/restore başarı dönmez; mevcut encrypted/tamper testleri geçer.'),
 ('4','P2','MP-QA-003 backend null validation + MP-QA-004 düğme event katmanı','Eksik/null400; üç dashboard gerçek tıklaması doğru hedef ve0 pageerror; yeni ID/Origin/tenant kontrolleri kalır.'),
 ('5','P2/P3','MP-QA-007–010 beş eski paketin güncel fixture/rol/UI sözleşmesi; MP-QA-006 metin','Ana63 tur0 fail; eski güvenlik/sahiplik kuralı gevşemeden hata sonrası tüm adımlar yürür.'),
 ('6','P2/P3','MP-QA-005 ortak modal ve MP-QA-012 mobil menü erişilebilirliği','Dialog/isim/ilk odak/Tab/Escape/odak dönüşü; gerçek kullanıcı/maliyet/modal testleri; adsız ikonlar çözülür.'),
 ('7','P2','MP-QA-011 inline event dönüşümü / CSP sertleştirme','script-src-attr none ile bütün ekran ve düğmeler geçer; XSS ve role kontrolleri tekrar geçer.'),
 ('8–9','P2','Veri yolları ve provider sınırlarının sadeleştirilmesi','İki IWmsProvider açık bridge/native isimlendirme; tek stok/rezervasyon sözleşmesi; event×target capability tablosu; freshness/policy/uzlaştırma örnekleri.'),
 ('10','P2','Denetim kapısı: tam regresyon, güncel test dashboard, endpoint×rol coverage envanteri','Sıfır P0/P1, uygulama500 negatifleri0, tüm required paketlerPASS; açık P2 için kabul/plan; kanıt arşivi.'),
 ('11+ (ayrı faz)','P1','DB tasarımı ve native kontrat hazırlığı','Tenant unique/fk/transaction/outbox/checkpoint/migration tasarımı; yetkili sözleşme ve anonim gerçek fixtures. Bu denetimde DB kurulmayacak.'),
 ('Sözleşme sonrası','P1','Sağlayıcı bazında adaptör ve kontrollü sandbox doğrulama','Önce read, sonra receipt/unknown/permission/quotas; native ERP/WMS/cargo/marketplace her işlem için kanıt. Süre sözleşme belli olmadan tahmin edilmedi.'),
 ('Dağıtım öncesi','P1','Güvenlik/performans/DR/CI üretim kabulü','Güncel CVE/SBOM, browser/a11y, uzun soak/queue yükü, gerçek restore, TLS/rollback ve en az bir uçtan uca sandbox mutabakatı.')])}

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
''')

write('FINAL_QA_SUMMARY.md',f'''# Nihai QA özeti

{stamp}

**Proje tamamlanmış değil; canlı kullanım için NO-GO.** Yerel mock geliştirme altyapısı kapsamlı, fakat stok kapasitesi ve veri kurtarmada yüksek öncelikli kusurlar mevcut. Bu ilk turda uygulama kodu düzeltilmedi; mevcut değişiklikler korundu. Gerçek DB, ERP/WMS veya pazaryeri kullanılmadı.

1. **Genel teknik durum:** .NET8 tek süreç JSON tabanlı entegrasyon uygulaması; statik JS UI,173 dosya/480 tür bildirimi/194 endpoint bildirimi. Build0 hata/uyarı. Kaynak SHA-256 değişimi0.
2. **Tamamlanan yerel özellikler:** ortak kimlik/sürüm/idempotency sözleşmeleri, manuel mapping kuyruğu, ERP salt okunur ürün korumaları, fiyat politikaları ve onay, tenant backend sınırları, mock webhook/outbox/retry ve worker hata izolasyonu. Tamamlandı etiketi bu test kapsamıyla sınırlı.
3. **Kısmi özellikler:** çok depo stok ile uçtan uca tahsis ilişkisi, merkezi event×target kapsamı, kanal native durum/finans mutasyonları, yedek/restore, erişilebilir UI ve tam regresyon uyumu.
4. **Eksikler:** doğrulanmış native Nebim/WMS adaptörleri; gerçek DB transaction/migration/HA; bütün native kargo/iptal/iade/belge/ödeme bağlantıları; üretim kapasite ve güvenlik/DR kabulü. DB ve gerçek bağlantılar bu fazda bilerek kapalı.
5. **Bütün doğrulanmış kusur grupları:** MP-QA-001 stok 30→tahsis 60; 002 yedekte 6 modül eksik; 003 credential{{}} HTTP 500; 004 üç dashboard düğmesi JS hatalı; 005 modal semantik/odak/Escape; 006 bozuk fiyat hata metni; 007 viewer credential bekleyen hesap testi; 008 eski inventory seçicisi; 009 iki eski WMS workflow UI testi; 010 operasyon fiyat/stok fixture beklentisi; 012 mobil menü erişilebilir adı eksik. Ayrıca 011 inline-handler CSP riski. Ayrıntı/tekrar üretme/fix önerisi/test [BUG_REGISTER](BUG_REGISTER.md) içinde.
6. **Güvenlik:** test edilen tenant/rol/CSRF/SSRF/webhook/secret/file/CSV sınırları geçti. Yeni yetki ihlali veya sömürülebilir XSS gösterilmedi; inline CSP savunması kısmi, yedek kaybı önemli, güncel CVE/penetrasyon taraması yok. “Güvenli olduğu ispatlandı” sonucu çıkarılamaz.
7. **Mimari problemler:** birden çok stok/rezervasyon modeli, aynı isimli farklı WMS sözleşmeleri, JSON dosyaları arasında transaction yok, frontend global override zinciri, yüksek veri hacminde tam snapshot maliyeti. Orchestrator mevcut EnterpriseSync’i yeniden kullanıyor; ek paralel kuyruk kurulmadı.
8. **Çalıştırılan mevcut test paketi sayısı:**61; ayrıca build ve egress guard ile63 ana komut. Yeni29 doğruluk kontrolü,126 ekran/viewport gözlemi ve performans istekleri ayrı sayıldı; farklı ölçümler toplanmadı.
9. **Sonuç sayıları:** ana63 komutta58 PASS/5 FAIL/0 atlandı;61 test paketi özelinde56/5/0. Yeni29 kontrol20 PASS/9 FAIL. Mevcut başarısızların beşi eski rol/UI/fiyat sözleşmesine uyumsuz; yeni kontroller gerçek uygulama kusurlarını kanıtlıyor. Firefox/WebKit ve canlı testler “atlandı0” içine başarı gibi dahil edilmedi; ayrı çalıştırılmadı listesinde.
10. **Kapsam / boşluk:** unit/contract/API/Playwright/E2E/negative/boundary/concurrency/replay/worker/recovery ve sınırlı security/load çalıştı. Her route×rol×payload veya line/branch coverage ölçülmedi. Uzun soak/DB/native sistemler, tam WCAG ve online dependency taraması yok.
11. **Pazaryerleri:** Trendyol, Hepsiburada, n11, Amazon TR, Boyner, Ticimax native adapter ve fixture paketi var; hepsinin ilgili paketleri PASS. Hepsi canlı sözleşme/izin/receipt bakımından Doğrulanmadı/Kısmi. HB package kimliği ve alias mapping/Yoksay güncel mock testte geçti; gerçek anonim payload onayı hâlâ gerekli. FBA/n11depom korunuyor; gerçek fiyat kapalı.
12. **Nebim/WMS:** interface/capabilities/mock modelleri ve akışları mevcut; native Nebim TODO açık, native WMS sağlayıcı seçimi yok. Hayali native endpoint üretilmedi. Fulfillment kapasite ve yedek kusuru giderilmeden sonraki faza hazır değil.
13. **Frontend/backend eksikleri:**42 ekran×3 genişlikte taşma0 ve açılış pageerror0; üç gerçek düğme hatalı, modal3 kontrol FAIL. Backend21 yeni kontrolde20 PASS/1 FAIL; credential null validation eksik. Beş eski paket tüm son adımlarına ulaşamadı.
14. **Önce düzeltilecekler:** P1 stok tahsis kapasitesi ve tam yedek/restore; ardından null500, dashboard,5 eski regresyonun uyarlanması, modal/mobil menü/CSP, kaynak/veri yolu sadeleştirme. P0 bulunmadı;2 P1,8 P2(risk dahil),2 P3.
15. **Gün planı:**1–2 tahsis;3 yedek;4 null/düğme;5 test fixture/rol/ekran uyumu;6 modal;7 CSP;8–9 veri/policy/provider sınırı;10 bütün yerel kabul testleri. Native/DB sözleşme fazı bundan sonra, süreleri yetkili sözleşme gelmeden kesinleştirilmedi. [Ölçülebilir backlog](PRIORITIZED_BACKLOG.md).
16. **Gerçek DB/canlı faz kriterleri:** sıfır P0/P1 ve yeşil regresyon, tenant transaction/unique/outbox tasarımı, provider sözleşmesi ve anonim fixtures, accepted/processed/unknown makbuz semantiği, kaynak sahipliği/onaylı politikalar, gerçek restore/rollback, güncel güvenlik taraması/TLS, yeterli SLO/uzun yük ve kontrollü sandbox mutabakatı. Bu denetimde bunlar yapılmış sayılmadı.
17. **Karar:** yerel mock geliştirme ve kusur düzeltme **GO**; gerçek bağlantı/üretim veya tamamlandı ilanı **NO-GO**. Test geçme oranı ürün tamamlanma yüzdesi değildir; ürün için yüzdelik tamamlanma tahmini verilmedi.

Performans:100/1k/10k sentetik ürün araması;10k HTTP concurrency1/4/8 p95≈27/45/97ms,124/124 yanıt200;30sn159/159 soak yanıtı başarılı. RAM örnekleri tepe≈432MiB ve GC sonrası gerileme; uzun dayanıklılık/leak veya üretim kapasite ispatı yok.

[Tüm sonuçlar](ALL_TEST_RESULTS.md) · [Özellikler](FEATURE_MATRIX.md) · [Hatalar](BUG_REGISTER.md) · [Eksikler](MISSING_FEATURES.md) · [Pazaryerleri](MARKETPLACE_REPORT.md) · [ERP/WMS](ERP_WMS_REPORT.md) · [Güvenlik](SECURITY_REPORT.md) · [Performans](PERFORMANCE_REPORT.md).
''')
print(json.dumps({'reports':14,'bugGroups':11,'additionalRisks':1,'mainCommands':runs['counts'],'newChecks':{'pass':20,'fail':9,'total':29},'uiObservations':len(ui['ui'])}))
