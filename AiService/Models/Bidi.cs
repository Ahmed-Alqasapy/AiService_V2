using System;
using System.Collections.Generic;
using System.Linq;

namespace AiService.Models
{


    public class BidiResult
    {
        public string Text { get; set; }
        public bool IsRtl { get; set; }

        public BidiResult(string text, bool isRtl)
        {
            Text = text;
            IsRtl = isRtl;
        }
    }

    /// <summary>
    /// Ported from https://github.com/mozilla/pdf.js/blob/master/src/core/bidi.js
    /// </summary>
    public static class Bidi
    {
        private static readonly string[] BaseTypes = {
        "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "S", "B", "S", "WS",
        "B", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN",
        "BN", "BN", "B", "B", "B", "S", "WS", "ON", "ON", "ET", "ET", "ET", "ON",
        "ON", "ON", "ON", "ON", "ON", "CS", "ON", "CS", "ON", "EN", "EN", "EN",
        "EN", "EN", "EN", "EN", "EN", "EN", "EN", "ON", "ON", "ON", "ON", "ON",
        "ON", "ON", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L",
        "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "ON", "ON",
        "ON", "ON", "ON", "ON", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L",
        "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L",
        "L", "ON", "ON", "ON", "ON", "BN", "BN", "BN", "BN", "BN", "BN", "B", "BN",
        "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN",
        "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN",
        "BN", "CS", "ON", "ET", "ET", "ET", "ET", "ON", "ON", "ON", "ON", "L", "ON",
        "ON", "ON", "ON", "ON", "ET", "ET", "EN", "EN", "ON", "L", "ON", "ON", "ON",
        "EN", "L", "ON", "ON", "ON", "ON", "ON", "L", "L", "L", "L", "L", "L", "L",
        "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L",
        "L", "ON", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L",
        "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L", "L",
        "L", "L", "L", "ON", "L", "L", "L", "L", "L", "L", "L", "L"
    };

        private static readonly string[] ArabicTypes = {
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "CS", "AL", "ON", "ON", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM",
        "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AN", "AN", "AN", "AN", "AN", "AN", "AN", "AN", "AN",
        "AN", "ET", "AN", "AN", "AL", "AL", "AL", "NSM", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM",
        "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "NSM", "ON", "NSM",
        "NSM", "NSM", "NSM", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL",
        "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL", "AL"
    };

        private static bool IsOdd(int i) => (i & 1) != 0;
        private static bool IsEven(int i) => (i & 1) == 0;

        private static int FindUnequal(string[] arr, int start, string value)
        {
            for (int j = start; j < arr.Length; ++j)
            {
                if (arr[j] != value)
                    return j;
            }
            return arr.Length;
        }

        private static void SetValues(string[] arr, int start, int end, string value)
        {
            for (int j = start; j < end; ++j)
                arr[j] = value;
        }

        private static char[] ReverseValues(char[] arr, int start, int end)
        {
            int j = end - 1;
            for (int i = start; i < j; ++i, --j)
            {
                char temp = arr[i];
                arr[i] = arr[j];
                arr[j] = temp;
            }
            return arr;
        }

        private static char MirrorGlyphs(char c)
        {
            return c switch
            {
                '(' => ')',
                ')' => '(',
                '<' => '>',
                '>' => '<',
                '[' => ']',
                ']' => '[',
                '{' => '}',
                '}' => '{',
                '\u00AB' => '\u00BB',
                '\u00BB' => '\u00AB',
                _ => c
            };
        }

        public static BidiResult BidiText(string str, int startLevel = -1)
        {
            bool isLtr = true;
            int strLength = str.Length;
            if (strLength == 0)
                return new BidiResult(str, false);

            var chars = str.ToCharArray();
            var types = new string[strLength];
            var oldtypes = new string[strLength];
            int numBidi = 0;

            for (int i = 0; i < strLength; ++i)
            {
                char charCode = str[i];
                string charType = "L";

                if (charCode <= 0x00ff)
                    charType = BaseTypes[charCode];
                else if (0x0590 <= charCode && charCode <= 0x05f4)
                    charType = "R";
                else if (0x0600 <= charCode && charCode <= 0x06ff)
                    charType = ArabicTypes[charCode & 0xff];
                else if (0x0700 <= charCode && charCode <= 0x08AC)
                    charType = "AL";

                if (charType == "R" || charType == "AL" || charType == "AN")
                    numBidi++;

                oldtypes[i] = types[i] = charType;
            }

            if (numBidi == 0)
                return new BidiResult(str, true);

            if (startLevel == -1)
            {
                if ((double)strLength / numBidi < 0.3)
                    startLevel = 0;
                else
                {
                    isLtr = false;
                    startLevel = 1;
                }
            }

            var levels = new int[strLength];
            for (int i = 0; i < strLength; ++i)
                levels[i] = startLevel;

            string e = IsOdd(startLevel) ? "R" : "L";
            string sor = e;
            string eor = sor;

            // W1
            string lastType = sor;
            for (int i = 0; i < strLength; ++i)
            {
                if (types[i] == "NSM")
                    types[i] = lastType;
                else
                    lastType = types[i];
            }

            // W2
            lastType = sor;
            for (int i = 0; i < strLength; ++i)
            {
                string t = types[i];
                if (t == "EN")
                    types[i] = (lastType == "AL") ? "AN" : "EN";
                else if (t == "R" || t == "L" || t == "AL")
                    lastType = t;
            }

            // W3
            for (int i = 0; i < strLength; ++i)
                if (types[i] == "AL")
                    types[i] = "R";

            // W4
            for (int i = 1; i < strLength - 1; ++i)
            {
                if (types[i] == "ES" && types[i - 1] == "EN" && types[i + 1] == "EN")
                    types[i] = "EN";
                if (types[i] == "CS" && (types[i - 1] == "EN" || types[i - 1] == "AN") && types[i + 1] == types[i - 1])
                    types[i] = types[i - 1];
            }

            // W5
            for (int i = 0; i < strLength; ++i)
            {
                if (types[i] == "EN")
                {
                    for (int j = i - 1; j >= 0; --j)
                    {
                        if (types[j] != "ET") break;
                        types[j] = "EN";
                    }
                    for (int j = i + 1; j < strLength; ++j)
                    {
                        if (types[j] != "ET") break;
                        types[j] = "EN";
                    }
                }
            }

            // W6
            for (int i = 0; i < strLength; ++i)
            {
                string t = types[i];
                if (t == "WS" || t == "ES" || t == "ET" || t == "CS")
                    types[i] = "ON";
            }

            // W7
            lastType = sor;
            for (int i = 0; i < strLength; ++i)
            {
                string t = types[i];
                if (t == "EN")
                    types[i] = (lastType == "L") ? "L" : "EN";
                else if (t == "R" || t == "L")
                    lastType = t;
            }

            // N1
            for (int i = 0; i < strLength; ++i)
            {
                if (types[i] == "ON")
                {
                    int end = FindUnequal(types, i + 1, "ON");
                    string before = sor;
                    if (i > 0) before = types[i - 1];
                    string after = eor;
                    if (end + 1 < strLength) after = types[end + 1];

                    if (before != "L") before = "R";
                    if (after != "L") after = "R";

                    if (before == after)
                        SetValues(types, i, end, before);

                    i = end - 1;
                }
            }

            // N2
            for (int i = 0; i < strLength; ++i)
                if (types[i] == "ON")
                    types[i] = e;

            // I1 & I2
            for (int i = 0; i < strLength; ++i)
            {
                string t = types[i];
                if (IsEven(levels[i]))
                {
                    if (t == "R") levels[i] += 1;
                    else if (t == "AN" || t == "EN") levels[i] += 2;
                }
                else
                {
                    if (t == "L" || t == "AN" || t == "EN") levels[i] += 1;
                }
            }

            // L2
            int highestLevel = -1;
            int lowestOddLevel = 99;
            for (int i = 0; i < strLength; ++i)
            {
                int level = levels[i];
                if (highestLevel < level) highestLevel = level;
                if (lowestOddLevel > level && IsOdd(level)) lowestOddLevel = level;
            }

            for (int level = highestLevel; level >= lowestOddLevel; --level)
            {
                int start = -1;
                for (int i = 0; i < strLength; ++i)
                {
                    if (levels[i] < level)
                    {
                        if (start >= 0)
                        {
                            ReverseValues(chars, start, i);
                            start = -1;
                        }
                    }
                    else if (start < 0)
                    {
                        start = i;
                    }
                }
                if (start >= 0)
                    ReverseValues(chars, start, strLength);
            }

            // Final string
            var result = new string(chars.Where(c => c != '<' && c != '>').ToArray());
            return new BidiResult(result, isLtr);
        }
    }
}
