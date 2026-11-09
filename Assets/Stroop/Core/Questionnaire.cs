using System;

namespace AvatarStroop.Core
{
    public static class Questionnaire
    {
        public const string Version = "custom-five-items-2026-10-26";
        static readonly string[] ids = { "focus", "distraction", "observed", "evaluated", "negative_evaluation_worry" };
        static readonly string[] en = {
            "During this block, I kept my attention on selecting the font colour.",
            "During this block, my attention was drawn away from selecting the font colour.",
            "During this block, I felt watched while completing the task.",
            "During this block, I felt that my task performance was being evaluated.",
            "During this block, I worried about receiving a negative judgment of my task performance."
        };
        static readonly string[] enAnchors = { "Strongly disagree", "Disagree", "Neither agree nor disagree", "Agree", "Strongly agree" };
        public const int Count = 5;
        public static string Id(int index) { ValidateIndex(index); return ids[index]; }
        public static string Item(int index, string language)
        {
            ValidateIndex(index); ValidateLanguage(language); return en[index];
        }
        public static string Instruction(string language)
        {
            ValidateLanguage(language); return "Please answer about the block you have just completed.";
        }
        public static string Anchor(int score, string language)
        {
            if(score < 1 || score > 5) throw new ArgumentOutOfRangeException("score");
            ValidateLanguage(language); return enAnchors[score-1];
        }
        static void ValidateIndex(int index) { if(index < 0 || index >= Count) throw new ArgumentOutOfRangeException("index"); }
        // Presentation is English-only. Do not reinterpret historical zh records
        // as English; the journal reader/exporter retain their raw language fields.
        static void ValidateLanguage(string value) { if(value != "en") throw new ArgumentException("Questionnaire presentation requires English (en); historical language identifiers are not converted"); }
    }
}
