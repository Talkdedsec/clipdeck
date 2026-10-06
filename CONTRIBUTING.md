# Katkı rehberi

Hata bildirimleri, öneriler ve düzeltmeler memnuniyetle karşılanır. Güvenlik açıklarını issue yerine [gizli olarak bildir](SECURITY.md).

## Geliştirme ortamı

- Windows 10 (2004) ya da 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (sürüm `global.json`'da)
- Visual Studio 2026, Rider ya da VS Code (C# Dev Kit)

```powershell
dotnet build                                             # derle
dotnet test tests\clipdeck.Tests\clipdeck.Tests.csproj   # testler
.\build.ps1                                              # dist\clipdeck.exe ve dist\clipdeck-kurulum.exe
```

**Gerçek geçmişine dokunmadan dene:** `CLIPDECK_DATA` ortam değişkeni ayrı bir veri klasörü kullandırır. Aynı anda yalnızca bir clipdeck çalışabildiği için kurulu olanı tepsiden kapat.

```powershell
$env:CLIPDECK_DATA = "$env:TEMP\clipdeck-dev"
dotnet run
```

**Yük ve tanıtım verisi:** `tests\clipdeck.Tests\LoadSeed.cs` içindeki testler yalnızca ortam değişkeni verilince çalışır:

| Değişken | Ne yapar |
| --- | --- |
| `CLIPDECK_SEED_DIR` | 10.000 metin, 300 resim, dosya ve snippet'lik büyük bir geçmiş üretir |
| `CLIPDECK_BACKUP_DIR` | Verilen geçmişi yedekleyip boş bir geçmişe geri yükler; süre ve belleği yazar |
| `CLIPDECK_DEMO_DIR` | Ekran görüntüleri için küçük ve gerçekçi bir geçmiş üretir |
| `CLIPDECK_DOWNGRADE_DIR` | Bir veritabanını eski (sürüm 1) biçime çevirir; geçiş süresini ölçmek için |
| `CLIPDECK_PANO_TESTI=1` | Gerçek panoyu kullanan testleri (`ClipboardTests`) açar. Panonun içeriği değişir; çalışan clipdeck'i önce kapat, yoksa test verisini geçmişine kaydeder |

```powershell
$env:CLIPDECK_SEED_DIR = "$env:TEMP\clipdeck-yuk"
dotnet test tests\clipdeck.Tests\clipdeck.Tests.csproj --filter "FullyQualifiedName~LoadSeed.Seed"
```

## Kod

- Çevredeki kodun düzenine uy: dosya kapsamlı namespace, 4 boşluk girinti, kısa ve odaklı metotlar.
- Yorumlar *neden*i anlatır; kodun zaten söylediğini tekrarlamaz.
- Arayüz metinleri Türkçe yazılır ve anahtar olarak kullanılır: `Loc.T("Kaydet")`, XAML'de `{ui:T Kaydet}`. İngilizcesi `Core\Loc.En.cs`'ye eklenir; eksik çeviri testte yakalanır.
- Pano içeriği, parola ya da başka kişisel veri asla günlüğe yazılmaz.
- Veritabanı biçimi değişirse eski veritabanlarını taşıyan bir geçiş ve bunun testi eklenir (`SchemaMigrationTests`).
- Yeni davranış için test ekle; mantığı arayüzden ayırmak test etmeyi kolaylaştırır.

## Commit ve pull request

- Commit mesajları kısa ve Türkçe: ne değişti, gerekirse ikinci paragrafta neden.
- Kullanıcının fark edeceği her değişiklik `CHANGELOG.md`'nin en üstündeki bölüme yazılır.
- Pull request'te CI (derleme + testler) ve CodeQL geçmelidir.

## Sürüm çıkarma

1. `clipdeck.csproj` içindeki `<Version>`'ı artır.
2. `CHANGELOG.md`'ye `## X.Y.Z — YYYY-AA-GG` başlıklı bölümü ekle.
3. Etiketle ve gönder: `git tag vX.Y.Z` ve `git push origin vX.Y.Z`.

**Sürüm** iş akışı testleri çalıştırır, paketi derler, etiketin `<Version>` ile aynı olduğunu denetler, notları CHANGELOG'dan alır, SHA-256 özetlerini ekler ve sürümü yayınlar.
