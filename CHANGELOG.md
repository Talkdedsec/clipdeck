# Değişiklikler

## 0.3.0 — 2026-10-06

**Yeni**
- Büyük önizleme (Space / F3): uzun metin, renklendirilmiş kod, resim ve içindeki yazı, dosya bilgileri, renk değerleri.
- Link temizleme ayarlardan seçilebilir: otomatik, menüden ya da kapalı.
- Kurulum dosyasına `--sessiz`: pencere açmadan kurar ya da günceller, mevcut seçimleri korur.

**İyileştirmeler**
- Yedek biçimi 2: yedek parça parça şifrelenip doğrudan diske yazılır; 1 GB'lık geçmiş ~150 MB bellekle yedeklenir (önceden birkaç GB gerekiyordu, 2 GB üstünde başarısız oluyordu). Eski yedekler açılmaya devam eder. Yedek alma ve geri yükleme arka planda çalışır.
- Panelin ilk açılışı ~1 sn'den ~80 ms'ye indi (farklı ölçekli monitörler önceden hazırlanıyor); emoji paneli tekrar açılışta anında geliyor.
- Kart numarası algılaması artık kart ağı ön ekine de bakıyor: Discord ID'leri ve zaman damgaları yanlışlıkla gizlenmiyor; Troy kartları tanınıyor.
- Önizlemede büyük resimler ekran boyutunda çözülüyor (4K bir ekran görüntüsü için ~33 MB yerine birkaç MB).
- Otomatik başlatma ayarı arayüzü bekletmeden uygulanıyor.

**Düzeltmeler**
- `hsl(1.2.3, …)` gibi bozuk bir renk metni, açılışta öğenin geçmişten düşmesine yol açıyordu.
- Ayarlar dosyası yazılırken çökme olursa tüm ayarlar sıfırlanabiliyordu; artık önce yan dosyaya yazılıyor.
- Silinen öğe çoklu seçimde kalıyordu.
- Yönetici modu normal moddayken kapatıldığında ya da program kaldırıldığında oturum açma görevi kalabiliyordu.
- Günlük dosyası 1 MB'ı geçince tamamen siliniyordu; artık önceki `.1` olarak saklanıyor.

## 0.2.0

- Emoji, kaomoji ve semboller (Win+.), renkli emoji ve ten rengi.
- Filtreler, çoklu seçim, snippet değişkenleri, hassas veri maskeleme, eski öğeleri otomatik silme.
- İngilizce arayüz, parolalı şifreli yedek, kurulum paketi, otomatik başlatma, yönetici modu.
- Özel arka plan fotoğrafı, vurgu rengi, kart saydamlığı ve panel boyutu.

## 0.1.0

- İlk sürüm: Win+V yerine şifreli pano geçmişi; metin, resim ve dosya; arama; sabitleme ve snippet'ler; düz metin ve dönüştürerek yapıştırma; resimden metin (OCR).
