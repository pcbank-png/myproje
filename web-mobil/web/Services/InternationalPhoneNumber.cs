namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Small dependency-free normalizer for the public registration flow.
    /// Stored values use E.164 form so international numbers stay comparable.
    /// Turkish local mobile formats remain accepted for backwards compatibility.
    /// </summary>
    public static class InternationalPhoneNumber
    {
        private const int MinDigits = 8;
        private const int MaxDigits = 15;

        public static bool TryNormalize(string? value, out string? normalized)
        {
            normalized = null;

            if (string.IsNullOrWhiteSpace(value))
                return true;

            var input = value.Trim();
            if (input.Length > 40)
                return false;

            var plusSeen = false;
            for (var i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (char.IsDigit(ch) || ch is ' ' or '-' or '(' or ')' or '.')
                    continue;

                if (ch == '+' && i == 0 && !plusSeen)
                {
                    plusSeen = true;
                    continue;
                }

                return false;
            }

            var digits = new string(input.Where(char.IsDigit).ToArray());
            if (digits.Length == 0)
                return false;

            if (input.StartsWith("+", StringComparison.Ordinal))
            {
                // Already international; only formatting characters were removed above.
            }
            else if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }
            else if (digits.Length == 11 && digits.StartsWith("05", StringComparison.Ordinal))
            {
                // 05XX XXX XX XX -> +90 5XX XXX XX XX
                digits = "90" + digits[1..];
            }
            else if (digits.Length == 10 && digits.StartsWith("5", StringComparison.Ordinal))
            {
                // 5XX XXX XX XX -> +90 5XX XXX XX XX
                digits = "90" + digits;
            }
            else if (digits.Length == 12 && digits.StartsWith("90", StringComparison.Ordinal))
            {
                // 905XX... is a common pasted Turkish international form without '+'.
            }
            else
            {
                // Non-Turkish local numbers are ambiguous. Ask for +country-code or 00 prefix.
                return false;
            }

            if (digits.Length < MinDigits || digits.Length > MaxDigits || digits[0] == '0')
                return false;

            normalized = "+" + digits;
            return true;
        }

        public static string[] GetLookupCandidates(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return Array.Empty<string>();

            var canonical = normalized.Trim();
            var digits = new string(canonical.Where(char.IsDigit).ToArray());
            var candidates = new HashSet<string>(StringComparer.Ordinal)
            {
                canonical,
                digits
            };

            if (canonical.StartsWith("+90", StringComparison.Ordinal) && digits.Length == 12)
            {
                // Before the global registration update, Turkish numbers were stored as 05XXXXXXXXX.
                candidates.Add("0" + digits[2..]);
            }

            return candidates.ToArray();
        }
    }
}
