# Kontrollü performans raporu

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

Tamamen sentetik ürünler; kullanıcı demo deposu değil, sistem temp altında ayrı MarketPilot kökü. Gerçek sağlayıcı ve DB yok. Sınır512MiB test-process working set ve p95>2sn durdurma koşulu. Yük az tutuldu; diğer süreçler durdurulmadı veya ayarları değiştirilmedi.

## 100 / 1.000 / 10.000 kayıt

Her boyutta mevcut küçük demo seed’ine N adet QA-SKU eklendi; dosya yazımı ve30 sayfalı arama ölçüldü. Her aramada sentetik toplam=N ve25 sonuç doğrulandı. Süreler tek lokal çalıştırma, ısınma/GC etkileri dahil; benchmark güven aralığı yok.

| Sentetik N | Yazma ms | Arama p50 ms | p95 ms | Working set MiB | Private MiB | CPU ms | JSON bytes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 100 | 2.95 | 0.77 | 0.96 | 45.3 | 18.3 | 46.88 | 159991 |
| 1000 | 9.50 | 1.36 | 1.69 | 56.0 | 29.6 | 46.88 | 1159696 |
| 10000 | 69.92 | 10.65 | 13.63 | 120.2 | 97.5 | 468.75 | 11192759 |

Arama filtre/sayfalama işlemi DemoStore üzerinde yapıldı; ERP veya pazaryeri roundtrip süresi değil. Tekil snapshot yazımı toplu10k ekleme;10k ayrı update performansı iddia edilmedi.

## HTTP yük / kontrollü kısa stress

10.000 sentetik kayıt, GET /api/products/search?search=QA%20synthetic&size=25; loopback istemci. Her yanıt tamamen okundu, sonuç bütünlüğü önceki katmanda test edildi. Eşzamanlılık aşamalı1→4→8; toplam124 HTTP/0 hata.

| Eşzamanlı | İstek | HTTP | p50 ms | p95 ms | Max ms | Son RAM MiB | CPU fark sn |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 20 | {'200': 20} | 11.23 | 27.47 | 27.47 | 282.6 | 0.219 |
| 4 | 40 | {'200': 40} | 39.64 | 44.83 | 45.68 | 314.5 | 0.766 |
| 8 | 64 | {'200': 64} | 75.44 | 97.25 | 102.42 | 359.1 | 1.156 |

Bu seviyede hata yok; “maksimum kapasite8” veya daha fazla kaldıramaz sonucu çıkarılmadı. Süreç CPU farkı Get-Process ile, RAM process working/private bytes ile ölçüldü; makinenin toplam CPU yüzdesi değil. Toplam batch elapsed ölçümü örnekleme komutu maliyetini de içerdiğinden kesin throughput kapasitesi raporlanmadı.

## Kısa soak

30.1 saniye, 159 istek, 0 hata;150ms istemci beklemesi. Örneklenen en yüksek working set 432.3MiB. Bellek yükselip GC sonrası geriliyor; tek30sn gözlemle leak var/yok denemez.512MiB sınırına gelinmedi.10k JSON’un tam snapshot modeli kısa sürede yüzlerce MiB’ye ulaştığından bu paylaşılan makinede daha büyük veri/yük ve uzun soak yapılmadı.100k/1m kayıt, saatler boyu test, çoklu tenant iş hacmi ve kuyruk saturasyonu **Doğrulanmadı**.

## Mimari kapasite riskleri

DemoStore filtrelemesi/serileştirmesi tam veri hacmiyle büyüyor; JSON’u clone/serialize etmek ve global lock yazmaları aynı süreçte kuyruklar. Çok dosyalı transaction ve process’ler arası row locking yok. Raporların/operations overview’un büyük order/queue setindeki davranışı bu ürün arama yüküyle ölçülmedi. Worker kapasite/retention ve dead-letter artışı için kontrollü ayrı benchmark gerekli. Native quota/timeouts ve DB indeksleri için şu an ölçüm yok.

Kanıt: [evidence/performance-probe.json](evidence/performance-probe.json), [evidence/ui-performance-results.json](evidence/ui-performance-results.json); test kodu Probes/Program.cs ve extended-checks.cjs. Bu sonuçlar yalnız yerel sentetik faza ait; canlı SLA veya üretim ölçek onayı değil.
