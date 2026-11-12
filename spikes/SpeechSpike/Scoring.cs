using System.Globalization;
using System.Text;

namespace SpeechSpike;

/// <summary>
/// Word and character error rates. Text is normalised first so that the score reflects the words
/// recognised and not punctuation or capital letters, which the engines handle differently.
/// </summary>
static class Scoring
{
    public static string Normalise(string text, string language)
    {
        text = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        if (language == "hi")
        {
            // हिन्दी and हिंदी are the same word: a half nasal consonant before a consonant is also
            // written as an anusvara. Fold both spellings into one.
            text = System.Text.RegularExpressions.Regex.Replace(text, "[ङञणनम]्(?=[क-ह])", "ं");
        }
        var builder = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch is '‌' or '‍') continue;                 // zero-width joiners
            if (language == "hi")
            {
                if (ch == '़') continue;                          // nukta: क़ and क count as the same letter
                if (ch == 'ँ') { builder.Append('ं'); continue; }   // chandrabindu written as anusvara
            }
            var category = char.GetUnicodeCategory(ch);
            bool keep = char.IsLetterOrDigit(ch) || ch == '\'' ||
                        category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            builder.Append(keep ? ch : ' ');
        }
        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (language == "en")
        {
            // Some engines write "Mr." where the reference spells out "mister". Same word.
            for (int i = 0; i < words.Length; i++)
                words[i] = words[i] switch { "mr" => "mister", "mrs" => "missus", "dr" => "doctor", _ => words[i] };
        }
        return string.Join(' ', words);
    }

    /// <summary>Edits needed to turn the hypothesis into the reference.</summary>
    public static int EditDistance<T>(IReadOnlyList<T> reference, IReadOnlyList<T> hypothesis) where T : IEquatable<T>
    {
        var previous = new int[hypothesis.Count + 1];
        var current = new int[hypothesis.Count + 1];
        for (int j = 0; j <= hypothesis.Count; j++) previous[j] = j;
        for (int i = 1; i <= reference.Count; i++)
        {
            current[0] = i;
            for (int j = 1; j <= hypothesis.Count; j++)
            {
                int substitute = previous[j - 1] + (reference[i - 1].Equals(hypothesis[j - 1]) ? 0 : 1);
                current[j] = Math.Min(substitute, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[hypothesis.Count];
    }

    public readonly record struct Tally(int WordEdits, int Words, int CharEdits, int Chars)
    {
        public double Wer => Words == 0 ? 0 : 100.0 * WordEdits / Words;
        public double Cer => Chars == 0 ? 0 : 100.0 * CharEdits / Chars;
        public static Tally operator +(Tally a, Tally b) =>
            new(a.WordEdits + b.WordEdits, a.Words + b.Words, a.CharEdits + b.CharEdits, a.Chars + b.Chars);
    }

    public static Tally Score(string reference, string hypothesis, string language)
    {
        string r = Normalise(reference, language), h = Normalise(hypothesis, language);
        string[] rw = r.Split(' ', StringSplitOptions.RemoveEmptyEntries), hw = h.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        char[] rc = r.Replace(" ", "").ToCharArray(), hc = h.Replace(" ", "").ToCharArray();
        return new Tally(EditDistance(rw, hw), rw.Length, EditDistance(rc, hc), rc.Length);
    }
}
