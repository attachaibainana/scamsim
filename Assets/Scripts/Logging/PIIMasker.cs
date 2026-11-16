using System.Text;
using System.Text.RegularExpressions;

namespace ScamSim.Logging
{
    public class MaskResult
    {
        public string Text = "";
        public int IdNumbers;
        public int PhoneNumbers;
        public int OtherNumbers;

        public bool AnyRedacted
        {
            get { return IdNumbers + PhoneNumbers + OtherNumbers > 0; }
        }
    }

    /// <summary>
    /// Replaces anything that looks like a real Thai national ID, phone number or other long
    /// number with [REDACTED]. Runs before a reply is logged AND before it is sent to the LLM,
    /// so real personal data never leaves the text field.
    /// When in doubt it redacts: losing a harmless number costs less than keeping a real one.
    /// </summary>
    public static class PIIMasker
    {
        public const string Redacted = "[REDACTED]";
        const int MinDigitsToRedact = 9;
        const int IdDigits = 13;
        const int MaxPrefixDigits = 3;

        // Separators that sit tightly inside one number or one date: ASCII and typographic dashes, dot, slash.
        const string Glue = @"[\-‐-―−./]";

        // Everything people put between the groups of one number: the glue above, whitespace and brackets.
        const string Separator = @"[\s\-‐-―−./()_]";

        // Digit groups (Arabic or Thai numerals) joined by up to three separators, e.g. "1 - 2345" or "(02) 123".
        static readonly Regex DigitRun = new Regex(
            @"\(?\+?\d(?:" + Separator + @"{0,3}\d)+", RegexOptions.Compiled);

        // A calendar date standing on its own (16-11-2026, 16/11/69, 2026-11-16). It is not personal data,
        // and it must not be added to the digits of a number written next to it.
        static readonly Regex Date = new Regex(
            @"(?<!\d)(?<!\d" + Glue + @")(?:" +
            @"(?:0?[1-9]|[12][0-9]|3[01])(?<a>" + Glue + @")(?:0?[1-9]|1[0-2])\k<a>(?:[0-9]{4}|[0-9]{2})" +
            @"|" +
            @"[0-9]{4}(?<b>" + Glue + @")(?:0?[1-9]|1[0-2])\k<b>(?:0?[1-9]|[12][0-9]|3[01])" +
            @")(?!\d)(?!" + Glue + @"\d)",
            RegexOptions.Compiled);

        public static MaskResult Mask(string input)
        {
            var result = new MaskResult();
            if (string.IsNullOrEmpty(input))
            {
                result.Text = input ?? "";
                return result;
            }

            result.Text = DigitRun.Replace(input, match => MaskRun(match.Value, result));
            return result;
        }

        static string MaskRun(string run, MaskResult result)
        {
            var output = new StringBuilder();
            int position = 0;
            foreach (Match date in Date.Matches(run))
            {
                MaskNumbers(run, position, date.Index, output, result);
                output.Append(date.Value);
                position = date.Index + date.Length;
            }
            MaskNumbers(run, position, run.Length, output, result);
            return output.ToString();
        }

        /// <summary>
        /// Splits run[start..end) into separate numbers and redacts the long ones.
        /// A group of 9+ digits is a whole number by itself; shorter groups next to each other are
        /// read as one number written with separators.
        /// </summary>
        static void MaskNumbers(string run, int start, int end, StringBuilder output, MaskResult result)
        {
            int copied = start;
            int numberStart = -1;
            int numberEnd = -1;
            int digits = 0;
            bool international = false;
            bool complete = false;

            int i = start;
            while (i < end)
            {
                if (!char.IsDigit(run[i]))
                {
                    i++;
                    continue;
                }

                int groupStart = i;
                while (i < end && char.IsDigit(run[i])) i++;
                int groupDigits = i - groupStart;
                bool longGroup = groupDigits >= MinDigitsToRedact;

                bool joins = numberStart >= 0 && !complete
                             && run.IndexOf('(', numberEnd, groupStart - numberEnd) < 0;
                // "+66 812345678": a short country code belongs to the long group after it.
                if (joins && longGroup) joins = international && digits <= MaxPrefixDigits;

                if (joins)
                {
                    digits += groupDigits;
                    numberEnd = i;
                    if (longGroup) complete = true;
                    continue;
                }

                if (numberStart >= 0) copied = Flush(run, copied, numberStart, numberEnd, digits, international, output, result);
                international = groupStart > 0 && run[groupStart - 1] == '+';
                numberStart = international ? groupStart - 1 : groupStart;
                numberEnd = i;
                digits = groupDigits;
                complete = longGroup;
            }

            if (numberStart >= 0) copied = Flush(run, copied, numberStart, numberEnd, digits, international, output, result);
            output.Append(run, copied, end - copied);
        }

        /// <summary>Writes one number to the output, redacted when it is long enough. Returns the new copy position.</summary>
        static int Flush(string run, int copied, int numberStart, int numberEnd, int digits, bool international,
            StringBuilder output, MaskResult result)
        {
            if (digits < MinDigitsToRedact) return copied;

            // "(02) 123-4567": the closing bracket is inside the number, so take the opening one too.
            if (numberStart > copied && run[numberStart - 1] == '('
                && run.IndexOf(')', numberStart, numberEnd - numberStart) >= 0)
                numberStart--;

            if (digits == IdDigits && !international) result.IdNumbers++;
            else if ((FirstDigitIsZero(run, numberStart, numberEnd) && digits <= 10) || (international && digits <= 12))
                result.PhoneNumbers++;
            else result.OtherNumbers++;

            output.Append(run, copied, numberStart - copied);
            output.Append(Redacted);
            return numberEnd;
        }

        static bool FirstDigitIsZero(string run, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                if (char.IsDigit(run[i])) return char.GetNumericValue(run[i]) == 0;
            }
            return false;
        }
    }
}
