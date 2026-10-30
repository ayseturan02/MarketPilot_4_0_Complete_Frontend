# Çok işletmeli erişim sözleşmesi

Mevcut `it_session` cookie, parola hash'leri, oturum süresi ve version ile oturum iptali korunur. Yeni veritabanı kurulmaz. `Data/local-accounts.json` kimlik doğrulama kayıtlarını, `Data/tenant-directory.json` tenant/organization/membership kayıtlarını tutar. Başlangıç demo tenant'ları default, demo-a ve demo-b'dir; bu kayıtlara sahip olmak kullanıcıya otomatik erişim vermez.

## Kavramlar

- Tenant: işletme güvenlik ve veri sınırı.
- Organization: aynı tenant içindeki organizasyon kaydı.
- User: mevcut yerel hesabın kimliği ve etkinlik bilgisi; parola/hash kullanıcı DTO'suna alınmaz.
- Membership: kullanıcı + tenant + organization + rol + etkinlik.
- Role: platform-admin, business-owner, business-admin, operations, warehouse, read-only.
- MarketplaceAccount: tenant'a ait mağaza hesabı; mevcut bağlantılar ayrı işletme deposunda saklanır.
- ErpConnection / WmsConnection: tenant'a ait bağlantı kaydı ve credential reference; gerçek bağlantı açmaz.

## Yetkiler

| Rol | Sınır |
|---|---|
| Platform yöneticisi | Tenant ve üyelik yönetimi. İş verileri aktif üyelik kapsamındadır. |
| İşletme sahibi | Kendi işletmesini, bağlantılarını ve yetkilerini yönetir. Platform rolü veremez. |
| İşletme yöneticisi | Kendi işletmesini ve daha düşük yetkili kullanıcıları yönetir; sahip/diğer yönetici rolünü değiştiremez. |
| Operasyon personeli | Sipariş operasyonları ve izinli WMS ilerletme/olayları. Bağlantı, politika ve kullanıcı yönetimi yapamaz. |
| Depo operasyon kullanıcısı | Depo/sipariş/sevkiyat okuma ve izinli WMS ilerletme/olayları. Finans ve yönetim erişimi yoktur. |
| Salt okunur kullanıcı | İzinli görüntüleme ve okuma amaçlı sorgular. Yazma/yönetim yapamaz. |

Eski admin/operator/viewer rolleri geriye uyumlu olarak normalize edilir. Oturumdaki tenant_role ve üyelik her istekte doğrulanır. Yetki/üyelik iptali veya parola değişimi eski oturumu geçersiz kılar. Tenant değişimi yalnız etkin üyelikle yeni imzalı cookie üretir; parola yenileme aktif tenant'ı korur.

## Backend sınırı

`TenantBoundary`, kimlik doğrulama sonrasında ve işletme servisleri seçilmeden önce çalışır. URL harf büyüklüğü kontrolleri atlatamaz. Tenant/business kaynak ve hedef alanları JSON içinde iç içe de olsa; query veya tenant header'larıyla gönderilse de aktif tenant ile uyuşmalıdır. Kullanıcı/tenant/üyelik kontrol işlemleri ayrıca rol ve hedef tenant yetkisini denetler. Header veya frontend seçimi tek başına tenant değiştirmez.

Her işletmenin katalog, sipariş, stok, bağlantı, webhook, kuyruk, audit, rapor ve yedek servisleri ayrı içerik köküyle oluşturulur. Tenant servisi eksikse ortak uygulama verisine dönülmez. Webhook tenant rotası, o tenant'ın imza/mağaza anahtarıyla doğrulanır; oturum çerezi yerine güvenilir callback kimliği kullanır.

Credential referansları `Data/credential-references.json` içinde işletmeye özel tutulur. ERP/WMS referans tipi ve tenant eşleşmesi zorunludur. Environment referansı tenant/sistem önekiyle sınırlıdır. Sır değeri arayüze veya snapshot'a dönmez; snapshot yalnız güvenli referans/bağlantı metadata'sı ve yapılandırılmış olma bilgisi içerir. Gerçek bağlantılar varsayılan olarak kapalıdır.

## Test

`npm.cmd run check:tenancy`: altı rol, yatay/dikey ihlal, büyük harfli rota, kaynak/hedef tenant sahteciliği, bağlantı ve sır izolasyonu, üyelik/switch, parola/oturum iptali, işletmeler arası katalog/sipariş/stok/CSV/worker/yedek izolasyonu ve yeniden başlatma. Geçici JSON verileri ve yerel HTTP kullanılır; canlı sistem veya veritabanı yoktur.
