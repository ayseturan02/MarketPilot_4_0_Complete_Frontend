# Hepsiburada paket taraması ve satış kimliği

Resmî [sipariş entegrasyonu sözleşmesi](https://developers.hepsiburada.com/tr/companies/hepsiburada?category=baslangic&guide=siparis-entegrasyonu-onemli-bilgiler&product=siparis-olusturma-entegrasyonu&version=v1.0&view=guide), ödenmiş sipariş kalemini `items.id`, paket kalemini `lineItemId` ile tanımlar. Paket numarası farklı bir kimliktir. Paket listelemede 24 saatlik pencere, en fazla 10 satır ve header üzerinden sayfalama kullanılır. Fixture'lar bu sözleşmeye göre kurgulanmıştır; gerçek müşteri yanıtı veya canlı API çağrısı kullanılmaz.

`MarketplaceOrderReader` stok toplama için Hepsiburada'nın ödenmiş kalemlerini ve paketlerini ayrı tarar. Diğer sipariş tüketicilerinde bu ek tarama varsayılan kapalıdır; paket gözlemi yeni ERP siparişi oluşturmaz. Paket yanıtında `lineItemId` eksikse genel `id` ile doldurulmaz; tarama görünür bir hata ile durur. Kargo barkodu ürün barkodu yerine kullanılmaz.

Stok hareketinin anahtarı satıcı kaynağı + Hepsiburada kalem kimliğidir. Paket değişimi bu kimliği değiştirmez. Aynı kalem birden fazla paketteyse paket miktarları birleştirilir; ödeme/paket görünümleri birlikte varsa miktarları uyuşmalıdır. SKU/sipariş kimliği veya miktar çelişirse dağıtım durur. Yeni görülen, sipariş zamanı bilinmeyen paket tarihsel satış sayılıp otomatik düşülmez; mutabakat bekler.

Tekil olarak eşleşen eski stok kimliği yeni kimliğe taşınır; eski kayıtta aynı kalem için birden fazla kimlik varsa sessiz düzeltme veya ikinci stok düşümü yapılmaz. Önceden verilen Yoksay kararı korunur. Paket bozma, tek başına satış iptali sayılmaz.

## Katalog eşleştirmesi ve Yoksay

Açık katalog bağlantısı ham kanal SKU'sundan önce gelir. Bir bağlantı mevcut fakat hedef stok ürününü bulamıyor veya bağlantı belirsizse ham SKU'ya geri dönülmez. Eşleşmeyen satışlar işlenmemiş kalır.

Bağlantılar ekranında eşleşmeyen satışlar, Yoksay ve yeniden değerlendirme işlemi gösterilir. Yoksay onay gerektirir ve ilgili satışı stok kaynağında takip edilmeyen olarak işaretler; stok düşmez. Katalog eşleştirmesi düzeltildiğinde aynı bekleyen kayıt yeniden değerlendirilir. UI yönetim düğmesini yalnız yetkili rolde gösterir; backend tenant/rol korumaları da uygulanır.

## Testler

`npm.cmd run check:stock-actions`

- Native mapper fixture'ları: farklı paket/kalem ID, eksik `lineItemId`, kargo barkodu ayrımı, header sayfalama ve limit.
- Stok regresyonu: ödeme → paket, iki paket, iki görünümün örtüşmesi, tekrar tarama, miktar çelişkisi, eksik kimlik, eski kayıt geçişi, açık eşleştirme önceliği ve belirsizlikte blokaj.
- Tarayıcı: gerçek eşleştirme formundan hedef ERP ürünü seçme, Yoksay iptal/onay diyaloğu, doğru üründen yalnız bir stok düşümü, yoksayılan satışta sıfır değişim ve tekrar taramada çift hareket olmaması. Geçici JSON fixture'ları ve mock mağazalar kullanılır.
