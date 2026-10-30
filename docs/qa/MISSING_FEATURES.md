# Eksik ve doğrulanmamış özellikler

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

Eksik, bilerek kapalı ve doğrulanmadı birbirinden ayrıldı. Gerçek DB/Nebim/WMS bağlantısının olmaması kullanıcı sınırına uygun; bu fazda “açılması gereken hata” değil. Yerel mock fazda kapatılması gereken gerçek hatalar ayrıca belirtiliyor.

| ID | Durum | İş | Gereken kanıt / tamamlanma ölçütü |
| --- | --- | --- | --- |
| M-01 | Eksik / kasıtlı bekliyor | Native Nebim adaptörü ve doğrulanmış transport/yazma/sorgu/error sözleşmesi | Yetkili teknik belge, örnek ve test kurulumu; hayali endpoint yok. |
| M-02 | Eksik / kasıtlı bekliyor | Seçilmiş native WMS sağlayıcı adaptörü | Depo/capability, rezervasyon ve receipt/freshness sözleşmesi. |
| M-03 | Kısmi / kusurlu | Tek otoriteyle uçtan uca atomik stok tahsisi | MP-QA-001, aynı fiziksel bakiye için canonical/operational/workflow rezervasyon ilişkisinin tekilleşmesi. |
| M-04 | Hatalı | Bütün modülleri kapsayan sürümlü yedek/restore | MP-QA-002; arşivin şifreli olması yeterli değil. |
| M-05 | Doğrulanmadı | Altı sağlayıcıda gerçek auth/izin/payload/receipt ve native yazma | Sandbox/anonim payload ve sağlayıcı onayı; özellikle HB paket/kalem kimliği ve Ticimax WSDL sürümü. |
| M-06 | Kısmi / kasıtlı kapalı | Gerçek ERP fiyat yayınlama | Politika hesabı mevcut; gerçek gönderim bu fazda açılmamalı. Onaylı native transport ayrıca geliştirilip test edilmeli. |
| M-07 | Kısmi | Her olay×hedef için açık capability/mapper ve tamamlanmış durum yayınlama | 12 olay adı var, bütün kombinasyonlar yazmaya uygun değil; mevcut safe-block doğru. |
| M-08 | Kısmi | Stok tazelik SLA ve tüm kaynaklar arası otomatik olmayan uzlaştırma yönetimi | Onay/TTL/çatışma inceleme, source checkpoint lag, stok yayını durdurma ve kurtarma prosedürü. |
| M-09 | Kısmi | Native e-fatura/e-arşiv/ödeme/geri ödeme/taşıyıcı etiketi | Yerel taslak/referans/demo ile gerçek mali belge/para hareketi ayrılmalı. Uydurma belge yok. |
| M-10 | Eksik / ertelenmiş | DB şeması, migration, unique constraint, transaction/outbox ve backup/PITR | Bu denetimde gerçek DB kurulmadı; tasarım ve izole DB test fazı ayrıca yetkilendirilmeli. |
| M-11 | Eksik | Çok süreçli HA/worker lease/rolling upgrade | Şimdiki JSON lock tek süreç garantisi; dağıtık yürütücü eklemeden mevcut kuyruğun DB tasarımı. |
| M-12 | Kısmi | Tam gözlemlenebilirlik / alarm / dead-letter operasyonu | Local trace/audit var; merkezi metrik, on-call alarm, SLO, retention ve DR runbook testi tamamlanmadı. |
| M-13 | Hatalı / kısmi | Güvenilir tam regresyon ve bütün API×rol matrisi | 5 eski paket fail; hata sonrası adımlar kapsam dışı. Yeni negatiflerde gerçek kusurlar var; line/branch coverage ölçümü yok. |
| M-14 | Kısmi | Tarayıcı/erişilebilirlik | Chrome var; Firefox/WebKit/Edge/Safari, screenreader, kontrast, keyboard kapsamı eksik; modal bug. |
| M-15 | Doğrulanmadı | Güncel dependency/SAST/DAST/container güvenlik kapıları | Offline cache0 açık sonucu güncel CVE güvencesi değil; SBOM/advisory freshness gerek. |
| M-16 | Doğrulanmadı | Üretim benzeri yük/uzun soak/queue kapasitesi | 10k arama/8 istemci/30sn küçük probe var; çok tenant ve gerçek quotas yok. |
| M-17 | Kısmi | CI artifact/test raporu ve deploy/rollback/TLS tatbikatı | CI YAML var, local Compose config geçti; Linux runner, image, TLS ve restore rollback çalıştırılmadı. |
| M-18 | Kısmi | Credential yaşam döngüsü ve privacy/retention politikası | Env/key-per-file var; sağlayıcı native bağlama, rotasyon/iptal, scoped metadata ve PII retention işletme onayına ihtiyaç duyar. |

Ada göre ürün eşleştirme, uydurma para birimi/fiyat, WMS onaysız iade stoğu, kör belirsiz yazma tekrarı veya hayali Nebim endpoint’i bu eksikleri tamamlamanın yolu değildir. Kaynak belirsizse inceleme kuyruğu ve açık capability sınırı korunmalı.
