using System.Text;

namespace WebAutomater.Core.Documents;

public enum IdentifierKind
{
    TcKimlik,
    VergiNo,
}

/// <summary>
/// Metinde geçen bir numara. <see cref="IsValid"/>: resmi kontrol hanesi hesabı tutuyor mu.
/// <see cref="Start"/> ve <see cref="Length"/>: numaranın metindeki yeri (araya giren boşluklar dahil).
/// </summary>
public sealed record IdentifierMatch(string Value, IdentifierKind Kind, bool IsValid, int Start, int Length);

/// <summary>
/// Metindeki 11 haneli (T.C. kimlik) ve 10 haneli (vergi) numaraları bulur.
/// Tek başına duran her 10/11 haneli sayı listelenir; hatalı eşleşme ihtimali yüksek olan biçimler
/// (harfe/tireye bitişik, boşluklarla bölünmüş) yalnızca kontrol hanesi tutuyorsa kabul edilir.
/// </summary>
public static class IdentifierFinder
{
    public static IEnumerable<IdentifierMatch> Find(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            var digits = new StringBuilder();
            var spaced = false;
            while (i < text.Length)
            {
                if (char.IsAsciiDigit(text[i]))
                {
                    digits.Append(text[i++]);
                }
                else if (IsInlineSpace(text[i]) && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    spaced = true;
                    i++;
                }
                else
                {
                    break;
                }
            }

            foreach (var match in Classify(text, start, i, digits.ToString(), spaced))
                yield return match;
        }
    }

    private static IEnumerable<IdentifierMatch> Classify(string text, int start, int end, string digits, bool spaced)
    {
        if (spaced)
        {
            if (digits.Length is 10 or 11 && IsChecksumValid(digits) && !IsGlued(text, start, end))
            {
                yield return Create(digits, start, end - start);
                yield break;
            }

            // Bütünü numara değil: "Tel 12345678901 2024" gibi yan yana duran parçalara ayrı ayrı bak.
            var position = start;
            while (position < end)
            {
                var partStart = position;
                while (position < end && char.IsAsciiDigit(text[position]))
                    position++;
                if (ClassifySolid(text, partStart, position) is { } match)
                    yield return match;
                position++;
            }

            yield break;
        }

        if (ClassifySolid(text, start, end) is { } solid)
            yield return solid;
    }

    private static IdentifierMatch? ClassifySolid(string text, int start, int end)
    {
        if (end - start is not (10 or 11))
            return null;
        if (IsDecimalPart(text, start, end))
            return null;
        var value = text[start..end];
        if (IsGlued(text, start, end) && !IsChecksumValid(value))
            return null;
        return Create(value, start, end - start);
    }

    private static IdentifierMatch Create(string digits, int start, int length) =>
        new(digits, digits.Length == 11 ? IdentifierKind.TcKimlik : IdentifierKind.VergiNo, IsChecksumValid(digits), start, length);

    private static bool IsInlineSpace(char c) => c is ' ' or ' ' or '\t';

    private static bool IsDecimalPart(string text, int start, int end) =>
        (end + 1 < text.Length && text[end] is ',' or '.' && char.IsAsciiDigit(text[end + 1])) ||
        (start >= 2 && text[start - 1] is ',' or '.' && char.IsAsciiDigit(text[start - 2]));

    // Harf, tire, eğik çizgi veya alt çizgiye bitişik sayılar genelde belge/fatura/ETTN parçasıdır.
    private static bool IsGlued(string text, int start, int end) =>
        (start > 0 && IsGlueChar(text[start - 1])) || (end < text.Length && IsGlueChar(text[end]));

    private static bool IsGlueChar(char c) => char.IsLetter(c) || c is '-' or '/' or '_' or '\\';

    public static bool IsChecksumValid(string digits) => digits.Length switch
    {
        11 => IsValidTcKimlik(digits),
        10 => IsValidVergiNo(digits),
        _ => false,
    };

    public static bool IsValidTcKimlik(string v)
    {
        if (v.Length != 11 || !v.All(char.IsAsciiDigit) || v[0] == '0')
            return false;
        int D(int i) => v[i] - '0';
        var odd = D(0) + D(2) + D(4) + D(6) + D(8);
        var even = D(1) + D(3) + D(5) + D(7);
        var tenth = ((odd * 7 - even) % 10 + 10) % 10;
        var eleventh = (odd + even + D(9)) % 10;
        return tenth == D(9) && eleventh == D(10);
    }

    public static bool IsValidVergiNo(string v)
    {
        if (v.Length != 10 || !v.All(char.IsAsciiDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var tmp = (v[i] - '0' + 9 - i) % 10;
            var part = tmp * (1 << (9 - i)) % 9;
            sum += tmp != 0 && part == 0 ? 9 : part;
        }

        return (10 - sum % 10) % 10 == v[9] - '0';
    }
}
