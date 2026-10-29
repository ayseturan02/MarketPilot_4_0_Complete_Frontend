# Ortak uygulama akışı

Ürün, sipariş, mağaza ve gönderi ekranları aynı sunucu kayıtlarını kullanır. Bir işlem kaydedildiğinde ilgili ekranların önbellekleri yenilenir; başka bir sekmede yapılan değişiklikler de sunucu sürümünden algılanır. Filtreler ve seçili ürünler korunur, doldurulan formlar otomatik yenilemeyle silinmez.

## Kullanım

1. Ürün kartları Nebim V3 kaynağından alınır; uygulamada yeni ürün oluşturulmaz. Aynı ürün Stok & Depolar, Barkod Merkezi, Kategori & Özellikler, Kârlılık ve Genel Bakış kayıtlarına yansır. Mağaza ilanları mevcut eşleştirme ve doğrulanan gönderim kuyruğuyla yönetilir.
2. Siparişler veya Toplama & Paketleme ekranından yerel sipariş oluşturun. Ürünler tüm katalogda aranır; fiyat ve yeterli stok sunucuda doğrulanır. Sipariş ve stok çıkışı tek kalıcı işlemde kaydedilir. İptal veya iade aynı stoğu iki kez geri eklemez.
3. Bir siparişe fatura taslağı, iade talebi, geri ödeme talebi veya iade belgesi taslağı bağlayın. İlgili fatura, taslak, eşleştirme, arşiv, iade ve geri ödeme ekranları aynı kayıt kimliğini kullanır. Taslak arşivlenince taslak listesinden çıkar ve arşivde görünür.
4. Yerel gönderi oluşturun. Kargo, takip ve etiket ekranları aynı gönderiyi gösterir; teslim durumu yerel siparişe de yansır. Sipariş detayından bağlı ürün, belge ve gönderilere ulaşılır.
5. Müşteri Soruları ekranından demo sorusunu yanıtlayın veya İade ekranından mevcut demo talebini onaylayın. İşlem ilgili mağazanın ortak kuyruğunda yürür; mağaza merkezi, finans hareketleri ve işlem geçmişi aynı sonucu okur.

İlgili ekranlar 25 kayıtlık sayfalar ve sunucu araması kullanır. Seçim formları ilk yüklenen 50 kayda bağlı değildir. Yeni mağazalar ortak sürüm bilgisini değiştirerek tüm mağaza seçicilerine yansır. Kullanıcı değişiklikleri de sürüm kontrolüne dahildir; görüntüleyiciler kayıt oluşturamaz veya değiştiremez.

## Kalıcılık ve dış servisler

Yerel işlem kayıtları, sipariş kimlikleri ve tekrar işlem koruması `demo-store.json` içindedir; mevcut şifreli yedek kapsamına dahildir. Başlangıçta mevcut ürünler ve siparişler korunur.

Yerel fatura taslağı resmî e-Fatura/e-Arşiv gönderimi değildir. Geri ödeme talebi para transferi sayılmaz. Gerçek belge gönderimi, ödeme ve gerçek pazaryeri yazmaları ilgili servislerin yapılandırılması ve izinleriyle yürütülür. Farklı satıcı hesaplarının veya para birimlerinin kayıtları birbirine karıştırılmaz.

Kontrol: `node tests/shared-workspace-http-checks.cjs` geçici verilerle ortak kimlikleri, stok işlemlerini, yeniden başlatma sonrası kalıcılığı, canlı yenilemeyi, form/filtre korunmasını, mobil görünümü ve rol sınırlarını sınar. `npm run check` tüm proje kontrollerini çalıştırır.
