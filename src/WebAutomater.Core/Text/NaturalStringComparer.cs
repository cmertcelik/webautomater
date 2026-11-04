using System.Globalization;

namespace WebAutomater.Core.Text;

/// <summary>
/// Dosya adlarını insanın beklediği sırayla karşılaştırır: içindeki sayılar sayı olarak ("Belge2" "Belge10"dan önce),
/// yazılar verilen kültüre göre büyük/küçük harf ayırmadan. .NET 10'daki CompareOptions.NumericOrdering'in yerine.
/// </summary>
public sealed class NaturalStringComparer(CultureInfo culture) : IComparer<string>
{
    private readonly CompareInfo _compare = culture.CompareInfo;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var xDigit = char.IsAsciiDigit(x[i]);
            var yDigit = char.IsAsciiDigit(y[j]);
            var xEnd = RunEnd(x, i, xDigit);
            var yEnd = RunEnd(y, j, yDigit);
            var xRun = x.AsSpan(i, xEnd - i);
            var yRun = y.AsSpan(j, yEnd - j);

            int result;
            if (xDigit && yDigit)
            {
                // Baştaki sıfırlar atılınca uzun olan sayı büyüktür; eşit uzunlukta haneler soldan karşılaştırılır.
                var xNum = xRun.TrimStart('0');
                var yNum = yRun.TrimStart('0');
                result = xNum.Length != yNum.Length ? xNum.Length.CompareTo(yNum.Length) : xNum.SequenceCompareTo(yNum);
            }
            else if (xDigit != yDigit)
            {
                // Sayıyla başlayan parça yazıdan önce gelir.
                return xDigit ? -1 : 1;
            }
            else
            {
                result = _compare.Compare(xRun, yRun, CompareOptions.IgnoreCase);
            }

            if (result != 0)
                return result;
            i = xEnd;
            j = yEnd;
        }

        if (i < x.Length || j < y.Length)
            return i < x.Length ? 1 : -1;
        // Yalnızca "01" / "1" ya da büyük/küçük harf farkı: sıra yine de kararlı olsun.
        return string.CompareOrdinal(x, y);
    }

    private static int RunEnd(string text, int start, bool digits)
    {
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]) == digits)
            end++;
        return end;
    }
}
