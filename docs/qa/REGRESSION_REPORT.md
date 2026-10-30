# Regresyon raporu

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

**Yeşil değil: 61 mevcut test paketinde56 PASS,5 FAIL.** Build ve dış bağlantı engeli ayrıca PASS. Başarısızlar silinmedi, orijinalleri değiştirilmedi; güvenlik veya sahiplik kuralı gevşetilmedi.

| Paket | Gerçek sonuç / sınıflandırma | İzleme |
| --- | --- | --- |
| accounts-http-checks.cjs | Viewer credential-profiles403; test200 ve koşulsuz JSON bekliyor. Yeni rol korumasına eski test uyumsuz. accounts-diagnostic.log ile istek kesinleştirildi. | MP-QA-007 |
| catalog-http-checks.cjs | inventory ekranında eski katalog tablo seçicisi timeout. | MP-QA-008 |
| clothing-catalog-http-checks.cjs | WMS labels ekranında eski workflow filtre seçicisi timeout. | MP-QA-009 |
| operational-preparation-http-checks.cjs | İlk yerel sipariş400; yetkili fiyat bilinmiyor. Beklenti200 ve sonraki eski fiziksel stok düşümü kuralı güncellenmeli. | MP-QA-010 |
| shared-workspace-http-checks.cjs | Satır85 modül döngüsünde wuState().result timeout. Dashboard satır83’ü geçti; bu dashboard yükleme sorunu diye raporlanmadı. WMS override sonrası eski workflow beklentisi. Tanı tekrarında da aynı satırda FAIL. | MP-QA-009 |

Geçen testler arasında canonical kimlik/birim/sürüm, ERP katalog salt okunur korumalar, fiyat politikaları, tenant sınırları, ERP/WMS orkestrasyonu, webhook ve worker izolasyonu var. Kullanıcının önceki Hepsiburada paket kimliği ve katalog eşleştirme/Yoksay eksikleri için güncel Hepsiburada/Synchronization birim paketleri ve catalog-stock-actions-http-checks.cjs **PASS**: alias eşleme satışı doğru ERP ürününe bağladı, Yoksay iptal/onayı ve restart/replay stok tek düşümü tarayıcıda denendi. Bu, gerçek Hepsiburada paket payload’ının sağlayıcı tarafından onayı değildir.

Yeni testler mevcut paketin göremediği MP-QA-001/002/003/004/005’i açığa çıkardı. Otomatik testler mevcut olsa bile iş kurallarının bütün veri yollarında doğru olduğu sonucu çıkarılamaz. Özellikle CanonicalStore atomik rezervasyon testi geçerken FulfillmentOrchestration aynı kapasiteyi iki siparişe tahsis edebiliyor.

Kapanış sırası: P1 kapasite ve yedek → backend null/DOM düğme → rol/ekran/fiyat fixture regresyon uyarlaması → modal ve CSP. Her düzeltmede ilgili negatif probe geçmeli, ardından mevcut63 tur yeniden koşulmalı; bu denetimde uygulama düzeltmesi yapılmadı.

Kanıt: [evidence/test-results.json](evidence/test-results.json); her FAIL için aynı adlı .log; [BUG_REGISTER](BUG_REGISTER.md).
