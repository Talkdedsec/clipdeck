using System.Windows.Media;
using ClipDeck.Core;

namespace ClipDeck.Tests;

public class SensitiveTests
{
    [Theory]
    [InlineData("4111 1111 1111 1111", SensitiveKind.Card)]
    [InlineData("Kart: 4111-1111-1111-1111 son kullanma 12/29", SensitiveKind.Card)]
    [InlineData("TR33 0006 1005 1978 6457 8413 26", SensitiveKind.Iban)]
    [InlineData("DE89370400440532013000", SensitiveKind.Iban)]
    [InlineData("ghp_A1b2C3d4E5f6G7h8I9j0KlMnOpQrStUv", SensitiveKind.Secret)]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwx", SensitiveKind.Secret)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV", SensitiveKind.Secret)]
    [InlineData("x7#Qp9!vL2@mZ4$wR8^tB6", SensitiveKind.Secret)]
    [InlineData("Kj8#mP2@xQ9!wL4$", SensitiveKind.Secret)]
    [InlineData("aB3dE9fG2hJ7kL5mN8pQ", SensitiveKind.Secret)]
    public void Detects(string text, SensitiveKind expected) => Assert.Equal(expected, Sensitive.Detect(text));

    [Theory]
    [InlineData("4111 1111 1111 1112")]
    [InlineData("TR33 0006 1005 1978 6457 8413 27")]
    [InlineData("Yarın saat 15:00'te toplantı var, salon B.")]
    [InlineData("MyNamespace.MyClass.DoSomething()")]
    [InlineData("C:\\Users\\ali\\Documents\\Rapor2026_Final.docx")]
    [InlineData("https://github.com/dotnet/wpf/issues/91")]
    [InlineData("Telefon: 0532 123 45 67")]
    [InlineData("Password1")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("ali.veli2026@ornek.com.tr")]
    [InlineData("SiparisNo_2026_Ekim_Final")]
    [InlineData("getUserById(userId2)")]
    public void IgnoresOrdinaryText(string text) => Assert.Equal(SensitiveKind.None, Sensitive.Detect(text));

    static string WithLuhnDigit(string body)
    {
        for (char d = '0'; d <= '9'; d++)
        {
            var s = body + d;
            int sum = 0;
            for (int i = s.Length - 1, k = 0; i >= 0; i--, k++)
            {
                int v = s[i] - '0';
                if (k % 2 == 1 && (v *= 2) > 9) v -= 9;
                sum += v;
            }
            if (sum % 10 == 0) return s;
        }
        throw new InvalidOperationException();
    }

    [Theory]
    [InlineData("115337292549891686")] // Discord-style ID
    [InlineData("169657920000")] // millisecond timestamp
    [InlineData("80012345678901")]
    public void LuhnValidIdsAreNotCards(string body) => Assert.Equal(SensitiveKind.None, Sensitive.Detect(WithLuhnDigit(body)));

    [Fact]
    public void TroyCardIsDetected() => Assert.Equal(SensitiveKind.Card, Sensitive.Detect(WithLuhnDigit("979203000000000")));

    [Fact]
    public void MasksKeepOnlyTheTail()
    {
        Assert.Equal("•••• •••• •••• 1111", Sensitive.Mask("Kart: 4111 1111 1111 1111", SensitiveKind.Card));
        Assert.Equal("TR33 •••• •••• •••• 26", Sensitive.Mask("TR33 0006 1005 1978 6457 8413 26", SensitiveKind.Iban));
        Assert.StartsWith("gh••", Sensitive.Mask("ghp_A1b2C3d4E5f6G7h8I9j0KlMnOpQrStUv", SensitiveKind.Secret));
    }
}

public class SnippetVarsTests
{
    [Fact]
    public void FillsDateTimeAndClipboard()
    {
        var (text, back) = SnippetVars.Expand("{tarih} {saat} / {pano}", () => "PANO");
        Assert.Matches(@"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2} / PANO$", text);
        Assert.Equal(0, back);
    }

    [Fact]
    public void EnglishNamesWorkToo()
    {
        var (text, _) = SnippetVars.Expand("{date}|{clipboard}", () => "x");
        Assert.Matches(@"^\d{2}\.\d{2}\.\d{4}\|x$", text);
    }

    [Fact]
    public void CursorMarkerIsRemovedAndCounted()
    {
        var (text, back) = SnippetVars.Expand("Merhaba {imleç}! Nasılsın?", () => null);
        Assert.Equal("Merhaba ! Nasılsın?", text);
        Assert.Equal("! Nasılsın?".Length, back);
    }

    [Fact]
    public void CrLfCountsAsOneCaretStep()
    {
        var (text, back) = SnippetVars.Expand("a{cursor}b\r\nc", () => null);
        Assert.Equal("ab\r\nc", text);
        Assert.Equal(3, back);
    }

    [Fact]
    public void ClipboardIsReadOnlyOnce()
    {
        int calls = 0;
        SnippetVars.Expand("{pano}{pano}", () => { calls++; return "p"; });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void UnknownBracesStay() => Assert.Equal("{bilinmeyen} {", SnippetVars.Expand("{bilinmeyen} {", () => null).Text);
}

public class TextUtilTests
{
    [Theory]
    [InlineData("#FF8800", 0xFF, 0x88, 0x00)]
    [InlineData("#f80", 0xFF, 0x88, 0x00)]
    [InlineData("rgb(34, 139, 230)", 34, 139, 230)]
    [InlineData("rgba(34,139,230,0.5)", 34, 139, 230)]
    [InlineData("hsl(0, 100%, 50%)", 255, 0, 0)]
    public void ParsesColors(string text, byte r, byte g, byte b)
    {
        var brush = Assert.IsType<SolidColorBrush>(TextUtil.ParseColor(text));
        Assert.Equal(Color.FromRgb(r, g, b), Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B));
    }

    [Theory]
    [InlineData("#hashtag")]
    [InlineData("FF8800")]
    [InlineData("rgb(1,2)")]
    [InlineData("hsl(1.2.3, 50%, 50%)")]
    [InlineData("hsl(10, 5.5.5%, 50%)")]
    public void RejectsNonColors(string text) => Assert.Null(TextUtil.ParseColor(text));

    [Theory]
    [InlineData("https://www.github.com/x?y=1", "github.com")]
    [InlineData("www.ornek.com.tr/sayfa", "ornek.com.tr")]
    public void ParsesLinks(string text, string domain)
    {
        var (isLink, host) = TextUtil.ParseLink(text);
        Assert.True(isLink);
        Assert.Equal(domain, host);
    }

    [Theory]
    [InlineData("bir link: https://github.com")]
    [InlineData("https://a.com https://b.com")]
    public void SentenceIsNotALink(string text) => Assert.False(TextUtil.ParseLink(text).Item1);

    [Theory]
    [InlineData("function topla(a, b) {\n    return a + b;\n}")]
    [InlineData("{\"ad\": \"Ayşe\", \"yas\": 30}")]
    [InlineData("SELECT ad, soyad FROM kisiler WHERE yas > 30;\nORDER BY ad;")]
    [InlineData("<div class=\"kart\">\n  <span>Merhaba</span>\n</div>")]
    public void RecognisesCode(string text) => Assert.True(TextUtil.LooksLikeCode(text));

    [Theory]
    [InlineData("Yarın sabah markete gidip ekmek, süt ve yumurta alacağım.")]
    [InlineData("Toplantı notları:\n- bütçe onaylandı\n- yeni ekip üyesi pazartesi başlıyor")]
    public void ProseIsNotCode(string text) => Assert.False(TextUtil.LooksLikeCode(text));

    [Fact]
    public void NormalizeFoldsTurkishLetters()
    {
        Assert.Equal("sifre isik cicek ogle guzel", TextUtil.Normalize("Şifre IŞIK çiçek ÖĞLE güzel"));
        Assert.Equal("istanbul", TextUtil.Normalize("İstanbul"));
    }

    [Fact]
    public void CodePreviewRemovesCommonIndent()
    {
        var preview = TextUtil.CodePreview("\n        if (x)\n            y();\n");
        Assert.Equal("if (x)\n    y();", preview);
    }

    [Fact]
    public void FilesPreviewSummarisesLongLists()
    {
        var files = Enumerable.Range(1, 6).Select(i => $@"C:\a\dosya{i}.txt").ToArray();
        var preview = TextUtil.FilesPreview(files);
        Assert.StartsWith("dosya1.txt", preview);
        Assert.EndsWith("+2", preview);
    }
}

public class LinkCleanerTests
{
    [Theory]
    [InlineData("https://site.com/a?utm_source=x&utm_medium=y", "https://site.com/a")]
    [InlineData("https://site.com/a?id=5&utm_campaign=z&fbclid=abc", "https://site.com/a?id=5")]
    [InlineData("https://site.com/a?gclid=1&q=%C3%A7ay&x=1#bolum", "https://site.com/a?q=%C3%A7ay&x=1#bolum")]
    [InlineData("https://youtu.be/abc?si=TRACK&t=42", "https://youtu.be/abc?t=42")]
    [InlineData("https://www.youtube.com/watch?v=abc&si=TRACK", "https://www.youtube.com/watch?v=abc")]
    [InlineData("https://ornek.com/ara?si=gerekli", "https://ornek.com/ara?si=gerekli")]
    [InlineData("https://site.com/sayfa", "https://site.com/sayfa")]
    public void CleansUrls(string input, string expected) => Assert.Equal(expected, LinkCleaner.CleanUrl(input));

    [Fact]
    public void CleansLinksInsideText()
    {
        var text = "Şuna bak https://site.com/a?utm_source=x ve www.b.com/?fbclid=1 tamam";
        Assert.Equal("Şuna bak https://site.com/a ve www.b.com/ tamam", LinkCleaner.CleanText(text));
        Assert.True(LinkCleaner.CanClean(text));
        Assert.False(LinkCleaner.CanClean("https://site.com/a?id=5"));
    }

    [Theory]
    [InlineData("https://shop.example.com/item/42?color=red&utm_campaign=fall&fbclid=IwAR0xyz&size=M#reviews",
        "https://shop.example.com/item/42?color=red&size=M#reviews")]
    [InlineData("  https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=AbC&t=42\r\n", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42")]
    [InlineData("https://site.com/a?id=5", null)]
    [InlineData("Şuna bak https://site.com/a?utm_source=x", null)]
    [InlineData("https://site.com/a?utm_source=x\nhttps://b.com/?fbclid=1", null)]
    public void AutoModeCleansOnlyABareLink(string copied, string? expected) => Assert.Equal(expected, LinkCleaner.CleanBareLink(copied));
}

public class TextTransformsTests
{
    static Func<string, string> T(string name) => TextTransforms.All.Single(t => t.Name == name).Apply;

    [Fact]
    public void TurkishCasing()
    {
        Assert.Equal("İSTANBUL ILIK", T("BÜYÜK HARF")("istanbul ılık"));
        Assert.Equal("istanbul ılık", T("küçük harf")("İSTANBUL ILIK"));
    }

    [Fact]
    public void JsonRoundTrip()
    {
        var pretty = T("JSON güzelleştir")("{\"ad\":\"Ayşe\",\"diller\":[\"tr\"]}");
        Assert.Contains("\"ad\": \"Ayşe\"", pretty);
        Assert.Equal("{\"ad\":\"Ayşe\",\"diller\":[\"tr\"]}", T("JSON sıkıştır")(pretty));
    }

    [Fact]
    public void Base64RoundTrip() => Assert.Equal("çğış", T("Base64 çöz")(T("Base64 kodla")("çğış")));

    [Fact]
    public void LineTools()
    {
        Assert.Equal("a\r\nb\r\nc", T("Satırları sırala")("c\nb\na"));
        Assert.Equal("a\r\nb", T("Tekrarlayan satırları sil")("a\nb\na"));
        Assert.Equal("a b c", T("Tek satıra indir")(" a\n  b \r\nc "));
    }

    [Fact]
    public void InvalidInputExplains()
    {
        var ex = Assert.ThrowsAny<Exception>(() => T("JSON güzelleştir")("{bozuk"));
        Assert.Equal("Geçerli bir JSON değil.", TextTransforms.Explain(ex));
    }
}
