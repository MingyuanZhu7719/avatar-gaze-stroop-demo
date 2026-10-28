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
        static readonly string[] zh = {
            "在刚才这一组中，我把注意力保持在选择字体颜色这件事上。",
            "在刚才这一组中，我的注意力被从选择字体颜色这件事上引开了。",
            "在刚才这一组中，我做任务时有被注视的感觉。",
            "在刚才这一组中，我觉得自己的任务表现正在受到评价。",
            "在刚才这一组中，我担心自己的任务表现会受到负面评价。"
        };
        static readonly string[] enAnchors = { "Strongly disagree", "Disagree", "Neither agree nor disagree", "Agree", "Strongly agree" };
        static readonly string[] zhAnchors = { "非常不同意", "不同意", "中立（既不赞同也不反对）", "同意", "非常同意" };
        public const int Count = 5;
        public static string Id(int index) { ValidateIndex(index); return ids[index]; }
        public static string Item(int index, string language)
        {
            ValidateIndex(index); ValidateLanguage(language); return language == "en" ? en[index] : zh[index];
        }
        public static string Instruction(string language)
        {
            ValidateLanguage(language); return language == "en" ? "Please answer about the block you have just completed." : "请根据刚刚完成的这一组作答。";
        }
        public static string Anchor(int score, string language)
        {
            if(score < 1 || score > 5) throw new ArgumentOutOfRangeException("score");
            ValidateLanguage(language); return language == "en" ? enAnchors[score-1] : zhAnchors[score-1];
        }
        static void ValidateIndex(int index) { if(index < 0 || index >= Count) throw new ArgumentOutOfRangeException("index"); }
        static void ValidateLanguage(string value) { if(value != "en" && value != "zh") throw new ArgumentException("Language must be explicit"); }
    }
}
