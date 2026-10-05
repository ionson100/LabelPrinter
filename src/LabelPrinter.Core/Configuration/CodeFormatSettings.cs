using System;

namespace LabelPrinter.Core.Configuration
{
    /// <summary>
    /// Формат печатаемого кода.
    ///
    /// По умолчанию: 01{GTIN}21{R1}{GS}93{R2} — то есть
    ///     (01) 02345678987654  — GTIN-14, фиксированное значение
    ///     (21) R1              — серийный номер, 6 случайных символов
    ///     (0x1D)               — разделитель групп (FNC1 в символике)
    ///     (93) R2              — 4 случайных символа
    /// </summary>
    public sealed class CodeFormatSettings
    {
        /// <summary>Фиксированная часть AI(01) — GTIN-14.</summary>
        public string Gtin14 { get; set; }

        /// <summary>
        /// Шаблон кода. Плейсхолдеры: {GTIN} — фиксированный GTIN-14,
        /// {R1} и {R2} — случайные блоки, {GS} — разделитель групп 0x1D.
        /// </summary>
        public string CodeTemplate { get; set; }

        /// <summary>Алфавит случайной части кода.</summary>
        public string CodeAlphabet { get; set; }

        public int RandomPart1Length { get; set; }

        public int RandomPart2Length { get; set; }

        /// <summary>
        /// Передавать разделитель групп как &amp;#x1D; вместо сырого байта 0x1D.
        /// Нужно, только если принтер отвергает управляющий символ в атрибуте
        /// (формально XML 1.0 такое значение не допускает).
        /// </summary>
        public bool GroupSeparatorAsEntity { get; set; }

        public CodeFormatSettings()
        {
            Gtin14 = "02345678987654";
            CodeTemplate = "01{GTIN}21{R1}{GS}93{R2}";
            CodeAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            RandomPart1Length = 6;
            RandomPart2Length = 4;
            GroupSeparatorAsEntity = false;
        }

        public CodeFormatSettings Clone()
        {
            return (CodeFormatSettings)MemberwiseClone();
        }

        /// <summary>Приводит значения, заданные вручную, к рабочим диапазонам.</summary>
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Gtin14)) Gtin14 = "02345678987654";
            if (string.IsNullOrWhiteSpace(CodeTemplate)) CodeTemplate = "01{GTIN}21{R1}{GS}93{R2}";
            if (string.IsNullOrWhiteSpace(CodeAlphabet)) CodeAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            if (RandomPart1Length < 0) RandomPart1Length = 0;
            if (RandomPart2Length < 0) RandomPart2Length = 0;
        }
    }
}