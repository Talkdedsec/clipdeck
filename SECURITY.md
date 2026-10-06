# Güvenlik

clipdeck panoya kopyalanan her şeyi, yani zaman zaman parola, adres ve kişisel mesajları da saklar. Bu yüzden güvenlik açıkları önceliklidir.

## Açık bildirme

Lütfen açıkları herkese açık bir issue olarak **açma**. Bunun yerine deponun **Security → Report a vulnerability** bölümünden gizli olarak bildir. Mümkünse şunları ekle:

- Etkilenen sürüm (Ayarlar penceresinin altında yazar)
- Adım adım nasıl tekrarlanacağı
- Olası etkisi

Bildirime en kısa sürede dönülür; düzeltme yayınlanınca bildiren kişi isterse teşekkür notunda anılır.

## Koruma modeli

- Geçmişteki her içerik alanı AES-256-GCM ile şifrelenir. Ana anahtar diskte Windows DPAPI ile (yalnızca o Windows hesabı) korunur.
- Aynı içeriği tekrar kaydetmemek için anahtarlı özet (HMAC-SHA256) kullanılır; içeriğin kendisi tahmin edilebilir bir özet olarak saklanmaz.
- Yedekler kullanıcının parolasından PBKDF2-SHA256 (600.000 tur) ile türetilen anahtarla, 1 MiB'lık AES-GCM parçaları halinde şifrelenir; kesilmiş ya da değiştirilmiş dosyalar reddedilir.
- Program ağ erişimi yapmaz.

Kapsam dışı: aynı Windows hesabında çalışan kötü amaçlı yazılımlar (bu hesabın DPAPI anahtarına ve panosuna zaten erişebilir) ve bilgisayara fiziksel erişimi olan saldırganlar.
