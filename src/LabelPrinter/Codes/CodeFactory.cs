using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LabelPrinter.Codes
{
    /// <summary>
    /// Генерация кода этикетки по шаблону.
    ///
    /// Формат по умолчанию (задание):
    ///     01{GTIN}21{R1}{GS}93{R2}
    /// что в развёрнутом виде даёт три элемента GS1:
    ///     (01) 02345678987654   — GTIN-14, фиксированное значение
    ///     (21) R1               — серийный номер, 6 случайных символов
    ///     (93) R2               — 4 случайных символа
    ///
    /// Между элементами переменной длины обязателен разделитель групп
    /// (FNC1 в терминах символики, 0x1D в данных). Без него получатель прочитает
    /// один длинный элемент вместо двух, поэтому {GS} в шаблоне нельзя убирать.
    /// </summary>
    public static class CodeFactory
    {
        public const string GtinToken = "{GTIN}";
        public const string Part1Token = "{R1}";
        public const string Part2Token = "{R2}";

        /// <summary>Плейсхолдер разделителя групп в шаблоне кода.</summary>
        public const string GroupSeparatorToken = "{GS}";

        /// <summary>Разделитель групп GS1: FNC1 в символике, 0x1D в данных.</summary>
        public const char GroupSeparator = '\u001D';

        /// <summary>Как показывать разделитель групп в журнале и на форме.</summary>
        public const string GroupSeparatorEscape = "<gr>";

        /// <summary>
        /// Строит один код. Случайные блоки берутся из криптографического ГПСЧ —
        /// коды не должны перебираться или повторяться.
        /// </summary>
        public static string Build(Core.AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");

            var alphabet = (settings.CodeAlphabet ?? "").ToCharArray();
            if (alphabet.Length == 0) alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

            var template = settings.CodeTemplate;
            if (string.IsNullOrEmpty(template)) template = "01{GTIN}21{R1}{GS}93{R2}";

            var r1 = RandomBlock(alphabet, settings.RandomPart1Length);
            var r2 = RandomBlock(alphabet, settings.RandomPart2Length);

            return template
                .Replace(GtinToken, settings.Gtin14 ?? string.Empty)
                .Replace(Part1Token, r1)
                .Replace(Part2Token, r2)
                .Replace(GroupSeparatorToken, GroupSeparator.ToString());
        }

        /// <summary>Случайный блок заданной длины из алфавита.</summary>
        public static string RandomBlock(char[] alphabet, int length)
        {
            if (length <= 0) return string.Empty;

            var chars = new char[length];
            // Отбрасываем неравномерность: берём побайтово и отсекаем «хвост» диапазона.
            int limit = 256 - (256 % alphabet.Length);
            for (int i = 0; i < length; i++)
            {
                byte b;
                do { b = RandomByte(); } while (b >= limit);
                chars[i] = alphabet[b % alphabet.Length];
            }
            return new string(chars);
        }

        private static byte RandomByte()
        {
            var buffer = new byte[1];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            return buffer[0];
        }

        /// <summary>
        /// Оценка числа возможных кодов по длине случайных блоков.
        /// Показывается в журнале при старте, чтобы было видно запас уникальности.
        /// </summary>
        public static double Combinations(Core.AppSettings settings)
        {
            if (settings == null) return 0;
            var n = (settings.CodeAlphabet ?? "").Length;
            if (n < 2) return 0;

            double total = 1;
            int blocks = settings.RandomPart1Length + settings.RandomPart2Length;
            for (int i = 0; i < blocks; i++) total *= n;
            return total;
        }

        /// <summary>
        /// Проверка контрольной цифры GTIN-14 (модуль 10, веса 3/1 справа налево).
        /// Возвращает null, если всё в порядке, иначе — текст проблемы.
        /// </summary>
        public static string ValidateGtin14(string gtin)
        {
            if (string.IsNullOrEmpty(gtin)) return "GTIN-14 пуст.";
            if (gtin.Length != 14) return "GTIN-14 должен содержать 14 цифр, а содержит " + gtin.Length + ".";

            foreach (var ch in gtin)
            {
                if (ch < '0' || ch > '9') return "GTIN-14 должен состоять только из цифр.";
            }

            int sum = 0;
            for (int i = 0; i < 13; i++)
            {
                int digit = gtin[i] - '0';
                // Позиция 1 от контрольной цифры весит 3, далее веса чередуются 3, 1, 3, 1…
                int positionFromRight = 13 - i;
                sum += (positionFromRight % 2 == 1) ? digit * 3 : digit;
            }

            int expected = (10 - (sum % 10)) % 10;
            int actual = gtin[13] - '0';
            if (expected != actual)
            {
                return "контрольная цифра GTIN-14 не сходится: в шаблоне '" + actual + "', расчётная '" + expected + "'.";
            }
            return null;
        }

        /// <summary>Пример кода для журнала и проверки настроек.</summary>
        public static string Sample(Core.AppSettings settings)
        {
            return Build(settings);
        }

        /// <summary>
        /// Код в виде, удобном для чтения в журнале: элементы GS1 в скобках,
        /// разделитель групп показан как &lt;gr&gt;.
        ///
        /// Например: (01)02345678987654(21)0BH6SS&lt;gr&gt;(93)udtI
        ///
        /// Разделитель групп завершает элемент переменной длины — без этого
        /// «93» внутри значения AI(21) приняли бы за начало следующего элемента.
        /// </summary>
        public static string ToHumanReadable(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;

            var sb = new StringBuilder();
            int i = 0;
            int guard = 0;

            while (i < code.Length && guard++ < 500)
            {
                if (code[i] == GroupSeparator)
                {
                    sb.Append(GroupSeparatorEscape);
                    i++;
                    continue;
                }

                int aiLen = DetectAi(code, i);
                if (aiLen == 0)
                {
                    sb.Append(code, i, code.Length - i);
                    break;
                }

                var ai = code.Substring(i, aiLen);
                i += aiLen;

                int fixedLength = FixedValueLength(ai);
                int end;

                if (fixedLength > 0)
                {
                    end = Math.Min(i + fixedLength, code.Length);
                }
                else
                {
                    // Элемент переменной длины идёт до разделителя групп.
                    //
                    // Если разделитель где-то есть дальше, искать «замаскированный»
                    // внутри значения AI нельзя: в случайных символах легко встретить
                    // пару цифр, совпадающую с номером другого AI (например «12»
                    // внутри (21)). Поэтому при наличии разделителя решает именно он.
                    //
                    // Если разделителя нет — код записан в старом стиле, и границу
                    // элемента приходится угадывать по следующему известному AI.
                    int separator = code.IndexOf(GroupSeparator, i);

                    end = i;
                    while (end < code.Length)
                    {
                        if (code[end] == GroupSeparator) break;
                        if (separator < 0 && end > i && DetectAi(code, end) > 0) break;
                        end++;
                    }
                }

                sb.Append('(').Append(ai).Append(')').Append(code, i, end - i);
                i = end;
            }

            return sb.ToString();
        }

        private static int DetectAi(string code, int index)
        {
            int remaining = code.Length - index;
            for (int len = Math.Min(4, remaining); len >= 2; len--)
            {
                string candidate = code.Substring(index, len);
                if (AllDigits(candidate) && IsKnownAi(candidate)) return len;
            }
            return 0;
        }

        private static bool IsKnownAi(string ai)
        {
            switch (ai)
            {
                case "00": case "01": case "02": case "03": case "04":
                case "11": case "12": case "13": case "15": case "16": case "17":
                case "20": case "21": case "22": case "29":
                case "30": case "37":
                case "90": case "91": case "92": case "93": case "94": case "95":
                case "96": case "97": case "98": case "99":
                    return true;
                default:
                    return false;
            }
        }

        private static int FixedValueLength(string ai)
        {
            switch (ai)
            {
                case "00": return 18;
                case "01": return 14;
                case "02": return 13;
                case "03": return 12;
                case "04": return 13;
                case "11": case "12": case "13": case "15": case "16": case "17": return 6;
                default: return 0;
            }
        }

        private static bool AllDigits(string s)
        {
            foreach (var ch in s)
            {
                if (ch < '0' || ch > '9') return false;
            }
            return true;
        }

        public static string Describe(Core.AppSettings settings)
        {
            double combinations = Combinations(settings);
            return string.Format(
                CultureInfo.InvariantCulture,
                "шаблон «{0}», GTIN-14 «{1}», случайных символов {2}+{3}, алфавит {4} шт., возможных кодов ≈ {5:0.##e+0}. " +
                "Разделитель групп 0x1D{6}передаётся {7}",
                settings.CodeTemplate,
                settings.Gtin14,
                settings.RandomPart1Length,
                settings.RandomPart2Length,
                (settings.CodeAlphabet ?? "").Length,
                combinations,
                settings.CodeTemplate != null && settings.CodeTemplate.Contains(GroupSeparatorToken) ? " присутствует, " : " ОТСУТСТВУЕТ — ",
                settings.GroupSeparatorAsEntity ? "как &#x1D;" : "как символ 0x1D");
        }
    }
}
